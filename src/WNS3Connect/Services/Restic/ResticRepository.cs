using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZstdSharp;

namespace WNS3Connect.Services.Restic;

/// <summary>ID 32 byte (SHA-256) của blob / pack / file trong kho restic.</summary>
public readonly record struct BlobId(ulong A, ulong B, ulong C, ulong D)
{
    public static BlobId Parse(string hex)
    {
        Span<byte> b = stackalloc byte[32];
        if (hex.Length != 64 || Convert.FromHexString(hex, b, out _, out _) != System.Buffers.OperationStatus.Done)
            throw new FormatException("ID restic không hợp lệ: " + hex);
        return From(b);
    }

    public static BlobId From(ReadOnlySpan<byte> b) => new(
        BinaryPrimitives.ReadUInt64BigEndian(b), BinaryPrimitives.ReadUInt64BigEndian(b[8..]),
        BinaryPrimitives.ReadUInt64BigEndian(b[16..]), BinaryPrimitives.ReadUInt64BigEndian(b[24..]));

    public override string ToString() => $"{A:x16}{B:x16}{C:x16}{D:x16}";
}

/// <summary>Vị trí một blob trong pack (từ index/).</summary>
public readonly record struct BlobLocation(int Pack, long Offset, int Length, int UncompressedLength, bool IsTree);

public sealed class ResticNode
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("mode")] public long Mode { get; set; }
    [JsonPropertyName("mtime")] public string? MtimeRaw { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("linktarget")] public string? LinkTarget { get; set; }
    [JsonPropertyName("content")] public List<string>? Content { get; set; }
    [JsonPropertyName("subtree")] public string? Subtree { get; set; }

    [JsonIgnore] public bool IsDir => Type == "dir";
    [JsonIgnore] public bool IsFile => Type == "file";
    [JsonIgnore] public DateTimeOffset? Mtime => ResticRepository.ParseTime(MtimeRaw);
}

public sealed class ResticSnapshot
{
    public required string Id { get; init; }
    public string ShortId => Id[..8];
    public DateTimeOffset Time { get; init; }
    public required string Tree { get; init; }
    public string Hostname { get; init; } = "";
    public string Username { get; init; } = "";
    public List<string> Paths { get; init; } = new();
    public List<string> Tags { get; init; } = new();
    public long? TotalBytes { get; init; }

    private string? Tag(string prefix) => Tags.FirstOrDefault(t => t.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
    private IEnumerable<string> TagsOf(string prefix) => Tags.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t[prefix.Length..]);

    // ----- Thẻ của WebNowBackup: wnb, u:<user>, b:<mã bản>, t:backup|snapshot, part:home|db, hp:<home>, mp:<meta>, db:<tên>, dp:<thư mục lô> -----
    public bool IsWnb => Tags.Contains("wnb");
    public string WnbUser => Tag("u:") ?? "";
    public string WnbBackupId => Tag("b:") ?? "";
    public bool IsHome => Tags.Contains("part:home");
    public List<string> Databases => TagsOf("db:").ToList();
    public bool IsDatabase => Databases.Count > 0;

    public string KindText => !IsWnb ? string.Join(", ", Paths)
        : IsHome ? "Home + cấu hình"
        : IsDatabase ? "Database: " + string.Join(", ", Databases)
        : string.Join(", ", Paths);

    public string UserText => IsWnb ? WnbUser : Username;
    public string TimeText => Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string SizeText => TotalBytes is long b ? Models.S3Entry.FormatSize(b) : "";
    public string TagsText => string.Join(" ", Tags.Where(t => !(IsWnb && (t == "wnb" || t.StartsWith("u:") || t.StartsWith("part:") || t.StartsWith("db:")))));

    /// <summary>Thư mục mở sẵn khi duyệt: home của user (WNB), thư mục lô database, hoặc đường dẫn duy nhất đã sao lưu.</summary>
    public string StartPath
    {
        get
        {
            if (IsWnb && IsHome && Tag("hp:") is { Length: > 0 } hp) return hp;
            if (IsWnb && Tag("dp:") is { Length: > 0 } dp) return dp;
            if (Paths.Count == 1 && Paths[0].StartsWith('/') && !IsDatabase) return Paths[0];
            return "/";
        }
    }

    public string MetaPath => Tag("mp:") ?? "";
    public override string ToString() => $"{TimeText} {UserText} {KindText}";
}

/// <summary>
/// Trình đọc kho restic viết lại bằng C# (không cần cài restic): mở khóa bằng mật khẩu, đọc snapshot, index, cây thư mục
/// và khôi phục file. Chỉ đọc — không bao giờ ghi/khóa kho, nên dùng song song với máy chủ đang sao lưu cũng an toàn.
/// Hỗ trợ kho bản 1 và 2 (nén zstd).
/// </summary>
public sealed class ResticRepository
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };

    public IResticBackend Backend { get; }
    public ResticKey MasterKey { get; }
    public string RepoId { get; }
    public int Version { get; }
    public List<ResticSnapshot> Snapshots { get; private set; } = new();

    private readonly string _cacheDir;
    private readonly List<string> _packs = new();
    private Dictionary<BlobId, BlobLocation> _index = new();
    private Task? _indexTask;
    private readonly object _indexLock = new();
    private readonly ConcurrentDictionary<BlobId, List<ResticNode>> _trees = new();

    public int BlobCount => _index.Count;
    public int PackCount => _packs.Count;
    public bool IndexLoaded => _indexTask is { IsCompletedSuccessfully: true };

    private ResticRepository(IResticBackend backend, ResticKey key, string id, int version)
    {
        Backend = backend; MasterKey = key; RepoId = id; Version = version;
        _cacheDir = Path.Combine(AppPaths.LocalDir, "restic-cache", id);
    }

    /// <summary>Mở kho: thử mật khẩu với từng file trong keys/, đọc config. Sai mật khẩu → ResticWrongPasswordException.</summary>
    public static async Task<ResticRepository> OpenAsync(IResticBackend backend, string password, CancellationToken ct)
    {
        var keys = await backend.ListAsync("keys", ct);
        if (keys.Count == 0) throw new InvalidOperationException("Không thấy thư mục keys/ — đây không phải kho restic.");
        var pw = ResticCrypto.Utf8(password);
        ResticKey? master = null;
        foreach (var (name, _) in keys)
        {
            var raw = await backend.ReadAllAsync("keys/" + name, ct);
            master = await Task.Run(() => TryKey(raw, pw), ct);
            if (master != null) break;
        }
        if (master == null) throw new ResticWrongPasswordException();

        var cfgRaw = await backend.ReadAllAsync("config", ct);
        using var cfg = JsonDocument.Parse(DecodeUnpacked(master, cfgRaw, "config"));
        string id = cfg.RootElement.GetProperty("id").GetString() ?? throw new ResticIntegrityException("config thiếu id");
        int ver = cfg.RootElement.TryGetProperty("version", out var v) ? v.GetInt32() : 1;
        if (ver > 2) throw new NotSupportedException($"Kho restic phiên bản {ver} chưa được hỗ trợ.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{16,64}$")) throw new ResticIntegrityException("config id lạ");
        return new ResticRepository(backend, master, id, ver);
    }

    private static ResticKey? TryKey(byte[] raw, byte[] password)
    {
        using var doc = JsonDocument.Parse(raw);
        var r = doc.RootElement;
        if (r.GetProperty("kdf").GetString() != "scrypt") return null;
        int N = r.GetProperty("N").GetInt32(), rr = r.GetProperty("r").GetInt32(), p = r.GetProperty("p").GetInt32();
        var salt = Convert.FromBase64String(r.GetProperty("salt").GetString()!);
        var data = Convert.FromBase64String(r.GetProperty("data").GetString()!);
        var user = ResticKey.FromBytes(ResticCrypto.Scrypt(password, salt, N, rr, p, 64));
        byte[] plain;
        try { plain = ResticCrypto.Decrypt(user, data); }
        catch (ResticIntegrityException) { return null; }
        using var mk = JsonDocument.Parse(plain);
        var mac = mk.RootElement.GetProperty("mac");
        return new ResticKey
        {
            Encrypt = Convert.FromBase64String(mk.RootElement.GetProperty("encrypt").GetString()!),
            MacK = Convert.FromBase64String(mac.GetProperty("k").GetString()!),
            MacR = Convert.FromBase64String(mac.GetProperty("r").GetString()!)
        };
    }

    /// <summary>File "rời" (config, index, snapshot): JSON thô, hoặc byte 0x02 + zstd ở kho bản 2.</summary>
    public static byte[] DecodeUnpacked(ResticKey key, byte[] raw, string what)
    {
        var plain = ResticCrypto.Decrypt(key, raw, what);
        if (plain.Length == 0 || plain[0] == '{' || plain[0] == '[') return plain;
        if (plain[0] != 2) throw new ResticIntegrityException("định dạng lạ " + what);
        using var input = new MemoryStream(plain, 1, plain.Length - 1);
        using var z = new DecompressionStream(input);
        using var ms = new MemoryStream(plain.Length * 4);
        z.CopyTo(ms);
        return ms.ToArray();
    }

    // ================= File rời có cache trên máy (đã mã hóa, tên file = SHA-256 nội dung) =================

    private async Task<byte[]> ReadCachedAsync(string dir, string id, CancellationToken ct)
    {
        string local = Path.Combine(_cacheDir, dir, id);
        if (File.Exists(local))
        {
            try
            {
                var b = await File.ReadAllBytesAsync(local, ct);
                if (ResticCrypto.Hex(ResticCrypto.Sha256(b)) == id) return b;
            }
            catch (IOException) { }
        }
        var raw = await Backend.ReadAllAsync(dir + "/" + id, ct);
        if (ResticCrypto.Hex(ResticCrypto.Sha256(raw)) != id) throw new ResticIntegrityException($"{dir}/{id} (SHA-256 không khớp tên file)");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            await File.WriteAllBytesAsync(local + ".tmp", raw, ct);
            File.Move(local + ".tmp", local, true);
        }
        catch (IOException) { }
        return raw;
    }

    private void PruneCache(string dir, IEnumerable<string> keep)
    {
        try
        {
            string d = Path.Combine(_cacheDir, dir);
            if (!Directory.Exists(d)) return;
            var set = new HashSet<string>(keep);
            foreach (var f in Directory.EnumerateFiles(d))
                if (!set.Contains(Path.GetFileName(f))) File.Delete(f);
        }
        catch { }
    }

    // ================= Snapshot =================

    private sealed class SnapshotDto
    {
        [JsonPropertyName("time")] public string? Time { get; set; }
        [JsonPropertyName("tree")] public string? Tree { get; set; }
        [JsonPropertyName("paths")] public List<string>? Paths { get; set; }
        [JsonPropertyName("hostname")] public string? Hostname { get; set; }
        [JsonPropertyName("username")] public string? Username { get; set; }
        [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
        [JsonPropertyName("summary")] public SummaryDto? Summary { get; set; }
    }

    private sealed class SummaryDto
    {
        [JsonPropertyName("total_bytes_processed")] public long? TotalBytes { get; set; }
    }

    public async Task<List<ResticSnapshot>> LoadSnapshotsAsync(CancellationToken ct)
    {
        var files = await Backend.ListAsync("snapshots", ct);
        var result = new ConcurrentBag<ResticSnapshot>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (f, c) =>
        {
            var raw = await ReadCachedAsync("snapshots", f.Name, c);
            var dto = JsonSerializer.Deserialize<SnapshotDto>(DecodeUnpacked(MasterKey, raw, "snapshot " + f.Name), Json);
            if (dto?.Tree == null) return;
            result.Add(new ResticSnapshot
            {
                Id = f.Name,
                Time = ParseTime(dto.Time) ?? DateTimeOffset.MinValue,
                Tree = dto.Tree,
                Hostname = dto.Hostname ?? "",
                Username = dto.Username ?? "",
                Paths = dto.Paths ?? new(),
                Tags = dto.Tags ?? new(),
                TotalBytes = dto.Summary?.TotalBytes
            });
        });
        PruneCache("snapshots", files.Select(f => f.Name));
        Snapshots = result.OrderByDescending(s => s.Time).ToList();
        return Snapshots;
    }

    // ================= Index =================

    private sealed class IndexDto { [JsonPropertyName("packs")] public List<IndexPackDto>? Packs { get; set; } }
    private sealed class IndexPackDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("blobs")] public List<IndexBlobDto>? Blobs { get; set; }
    }
    private sealed class IndexBlobDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("offset")] public long Offset { get; set; }
        [JsonPropertyName("length")] public int Length { get; set; }
        [JsonPropertyName("uncompressed_length")] public int UncompressedLength { get; set; }
    }

    /// <summary>Nạp index (một lần, các lần gọi sau chờ chung). Lỗi thì lần gọi sau thử lại.</summary>
    public Task EnsureIndexAsync(CancellationToken ct)
    {
        lock (_indexLock)
        {
            if (_indexTask == null || _indexTask.IsFaulted || _indexTask.IsCanceled) _indexTask = LoadIndexAsync(ct);
            return _indexTask;
        }
    }

    private async Task LoadIndexAsync(CancellationToken ct)
    {
        var files = await Backend.ListAsync("index", ct);
        var parsed = new ConcurrentBag<IndexDto>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = ct }, async (f, c) =>
        {
            var raw = await ReadCachedAsync("index", f.Name, c);
            var dto = JsonSerializer.Deserialize<IndexDto>(DecodeUnpacked(MasterKey, raw, "index " + f.Name), Json);
            if (dto != null) parsed.Add(dto);
        });
        var packIds = new Dictionary<string, int>();
        var packs = new List<string>();
        var index = new Dictionary<BlobId, BlobLocation>();
        foreach (var dto in parsed)
            foreach (var p in dto.Packs ?? new())
            {
                if (!packIds.TryGetValue(p.Id, out int pi)) { pi = packs.Count; packs.Add(p.Id); packIds[p.Id] = pi; }
                foreach (var b in p.Blobs ?? new())
                    index.TryAdd(BlobId.Parse(b.Id), new BlobLocation(pi, b.Offset, b.Length, b.UncompressedLength, b.Type == "tree"));
            }
        PruneCache("index", files.Select(f => f.Name));
        lock (_indexLock) { _packs.Clear(); _packs.AddRange(packs); _index = index; }
    }

    private string PackPath(int pack) { string id = _packs[pack]; return "data/" + id[..2] + "/" + id; }

    private BlobLocation Locate(BlobId id) =>
        _index.TryGetValue(id, out var loc) ? loc : throw new ResticIntegrityException("không thấy blob " + id.ToString()[..8] + " trong index (kho thiếu dữ liệu?)");

    [ThreadStatic] private static Decompressor? _zstd;

    /// <summary>Giải mã (và giải nén) một blob, kiểm SHA-256 = ID.</summary>
    private byte[] DecodeBlob(BlobId id, BlobLocation loc, ReadOnlySpan<byte> raw)
    {
        var plain = ResticCrypto.Decrypt(MasterKey, raw, "blob " + id.ToString()[..8]);
        if (loc.UncompressedLength > 0)
        {
            var outBuf = new byte[loc.UncompressedLength];
            int n = (_zstd ??= new Decompressor()).Unwrap(plain, outBuf);
            if (n != outBuf.Length) throw new ResticIntegrityException("giải nén thiếu blob " + id.ToString()[..8]);
            plain = outBuf;
        }
        if (BlobId.From(ResticCrypto.Sha256(plain)) != id) throw new ResticIntegrityException("SHA-256 sai ở blob " + id.ToString()[..8]);
        return plain;
    }

    public async Task<byte[]> LoadBlobAsync(BlobId id, CancellationToken ct)
    {
        await EnsureIndexAsync(ct);
        var loc = Locate(id);
        var raw = await Backend.ReadAsync(PackPath(loc.Pack), loc.Offset, loc.Length, ct);
        return DecodeBlob(id, loc, raw);
    }

    // ================= Cây thư mục =================

    private sealed class TreeDto { [JsonPropertyName("nodes")] public List<ResticNode>? Nodes { get; set; } }

    public async Task<List<ResticNode>> LoadTreeAsync(string treeId, CancellationToken ct)
    {
        var id = BlobId.Parse(treeId);
        if (_trees.TryGetValue(id, out var cached)) return cached;
        var plain = await LoadBlobAsync(id, ct);
        var nodes = JsonSerializer.Deserialize<TreeDto>(plain, Json)?.Nodes ?? new();
        _trees[id] = nodes;
        return nodes;
    }

    /// <summary>Danh sách trong một thư mục của snapshot ("/" = gốc). Thư mục không có → null.</summary>
    public async Task<List<ResticNode>?> ListDirAsync(ResticSnapshot snap, string path, CancellationToken ct)
    {
        var nodes = await LoadTreeAsync(snap.Tree, ct);
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = nodes.FirstOrDefault(n => n.Name == part && n.IsDir && n.Subtree != null);
            if (d == null) return null;
            nodes = await LoadTreeAsync(d.Subtree!, ct);
        }
        return nodes;
    }

    // ================= Khôi phục =================

    /// <summary>Đoạn liên tiếp trong một pack (nhiều blob kề nhau của cùng file → 1 request).</summary>
    private sealed record Segment(int Pack, long Offset, int Length, List<(BlobId Id, BlobLocation Loc)> Blobs);

    private const int MaxSegment = 8 * 1024 * 1024;

    private List<Segment> PlanSegments(IEnumerable<string> content)
    {
        var segs = new List<Segment>();
        Segment? cur = null;
        foreach (var hex in content)
        {
            var id = BlobId.Parse(hex);
            var loc = Locate(id);
            if (cur != null && cur.Pack == loc.Pack && cur.Offset + cur.Length == loc.Offset && cur.Length + loc.Length <= MaxSegment)
            {
                cur.Blobs.Add((id, loc));
                cur = cur with { Length = cur.Length + loc.Length };
                segs[^1] = cur;
            }
            else
            {
                cur = new Segment(loc.Pack, loc.Offset, loc.Length, new() { (id, loc) });
                segs.Add(cur);
            }
        }
        return segs;
    }

    /// <summary>Ghi nội dung file ra stream theo đúng thứ tự, tải trước tối đa <paramref name="window"/> đoạn song song.</summary>
    public async Task WriteFileAsync(ResticNode node, Stream output, Action<long>? onBytes, CancellationToken ct, int window = 6)
    {
        await EnsureIndexAsync(ct);
        if (node.Content == null || node.Content.Count == 0) return;
        var segs = PlanSegments(node.Content);
        var inflight = new Queue<Task<List<byte[]>>>();
        int next = 0;
        void Fill()
        {
            while (inflight.Count < window && next < segs.Count)
            {
                var s = segs[next++];
                inflight.Enqueue(Task.Run(async () =>
                {
                    var raw = await Backend.ReadAsync(PackPath(s.Pack), s.Offset, s.Length, ct);
                    var parts = new List<byte[]>(s.Blobs.Count);
                    foreach (var (id, loc) in s.Blobs)
                        parts.Add(DecodeBlob(id, loc, raw.AsSpan((int)(loc.Offset - s.Offset), loc.Length)));
                    return parts;
                }, ct));
            }
        }
        try
        {
            Fill();
            while (inflight.Count > 0)
            {
                var parts = await inflight.Dequeue();
                Fill();
                foreach (var p in parts)
                {
                    await output.WriteAsync(p, ct);
                    onBytes?.Invoke(p.Length);
                }
            }
        }
        catch
        {
            // Không để các request còn lại chạy ngầm ném lỗi không ai bắt.
            foreach (var t in inflight) _ = t.ContinueWith(x => _ = x.Exception, TaskScheduler.Default);
            throw;
        }
    }

    /// <summary>Một mục cần khôi phục: node + đường dẫn tương đối nơi ghi ra.</summary>
    public sealed record RestoreItem(ResticNode Node, string RelativePath);

    /// <summary>
    /// Duyệt cây (thư mục) → danh sách file/thư mục/symlink cần khôi phục, kèm tổng dung lượng.
    /// Đọc theo từng tầng, nhiều cây con song song (mỗi cây = 1 request nhỏ, thư mục sâu/nhiều nhánh vẫn nhanh).
    /// </summary>
    public Task<(List<RestoreItem> Items, long TotalBytes)> PlanRestoreAsync(IEnumerable<ResticNode> roots, Action<int>? onFound, CancellationToken ct) =>
        PlanRestoreAsync(roots.Select(r => new RestoreItem(r, r.Name)), onFound, ct);

    /// <summary>Như trên, mỗi gốc kèm đường dẫn tương đối riêng (vd kết quả tìm kiếm: giữ cây thư mục, không đè nhau).</summary>
    public async Task<(List<RestoreItem> Items, long TotalBytes)> PlanRestoreAsync(IEnumerable<RestoreItem> roots, Action<int>? onFound, CancellationToken ct)
    {
        var items = new List<RestoreItem>();
        long total = 0;
        var frontier = new List<RestoreItem>();
        void Add(RestoreItem it)
        {
            items.Add(it);
            if (it.Node.IsFile) total += it.Node.Size;
            if (it.Node.IsDir && it.Node.Subtree != null) frontier.Add(it);
        }
        foreach (var r in roots) Add(r);
        while (frontier.Count > 0)
        {
            var level = frontier.ToArray();
            frontier.Clear();
            var children = new List<ResticNode>[level.Length];
            await Parallel.ForEachAsync(Enumerable.Range(0, level.Length), new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
                async (i, c) => children[i] = await LoadTreeAsync(level[i].Node.Subtree!, c));
            for (int i = 0; i < level.Length; i++)
                foreach (var ch in children[i]) Add(new RestoreItem(ch, level[i].RelativePath + "/" + ch.Name));
            onFound?.Invoke(items.Count);
        }
        onFound?.Invoke(items.Count);
        return (items, total);
    }

    public static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.TrimEnd(' ', '.');
        if (n is "" or "." or "..") return "_";
        // Tên thiết bị của Windows (con, nul, com1…) không tạo được file.
        string stem = n.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && char.IsDigit(stem[3]))) n = "_" + n;
        return n;
    }

    /// <summary>Đường dẫn trên máy cho một đường dẫn trong snapshot; dài từ 240 ký tự thì thêm tiền tố \\?\ (Windows).</summary>
    public static string LocalPath(string dir, string rel)
    {
        string p = Path.Combine(new[] { Path.GetFullPath(dir) }.Concat(rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(SafeName)).ToArray());
        if (OperatingSystem.IsWindows() && p.Length >= 240 && !p.StartsWith(@"\\?\"))
            p = p.StartsWith(@"\\") ? @"\\?\UNC\" + p[2..] : @"\\?\" + p;
        return p;
    }

    public sealed class RestoreResult
    {
        public int Files, Dirs, Skipped, Failed;
        public long Bytes;
        public List<string> Errors { get; } = new();
        internal void Error(string msg) { lock (Errors) if (Errors.Count < 200) Errors.Add(msg); }
    }

    /// <summary>Một file đang khôi phục: các blob về không theo thứ tự, ghi thẳng vào đúng vị trí trong file tạm.</summary>
    private sealed class FileJob(RestoreItem item, string local)
    {
        public RestoreItem Item { get; } = item;
        public string Local { get; } = local;
        public string Part => Local + ".wnrpart";
        public long Size;
        public int Remaining;
        public long Written;
        public volatile bool Failed;
        public Microsoft.Win32.SafeHandles.SafeFileHandle? Handle;
        public readonly object Lock = new();
    }

    private const int MaxRange = 8 * 1024 * 1024;
    private const int MaxGap = 256 * 1024;

    /// <summary>
    /// Khôi phục ra thư mục trên máy (giống restorer của restic): gom mọi blob cần dùng theo pack, mỗi đoạn liền nhau
    /// trong pack tải 1 lần (kể cả khi blob thuộc nhiều file nhỏ khác nhau), giải mã rồi ghi vào đúng vị trí của từng file.
    /// File ghi qua .wnrpart rồi đổi tên, giữ giờ sửa. Symlink / thiết bị → bỏ qua (ghi vào danh sách lỗi).
    /// </summary>
    public async Task<RestoreResult> RestoreAsync(List<RestoreItem> items, string targetDir, Action<long>? onBytes, CancellationToken ct, int parallel = 8)
    {
        await EnsureIndexAsync(ct);
        var res = new RestoreResult();
        foreach (var it in items.Where(i => i.Node.IsDir))
        {
            Directory.CreateDirectory(LocalPath(targetDir, it.RelativePath));
            res.Dirs++;
        }

        // Lập kế hoạch: blob → các (file, vị trí) cần nó.
        var jobs = new List<FileJob>();
        var targets = new Dictionary<BlobId, List<(FileJob Job, long Offset)>>();
        var locs = new Dictionary<BlobId, BlobLocation>();
        foreach (var it in items.Where(i => i.Node.IsFile))
        {
            var job = new FileJob(it, LocalPath(targetDir, it.RelativePath));
            jobs.Add(job);
            long off = 0;
            var mine = new List<(BlobId, BlobLocation, long)>();
            try
            {
                foreach (var hex in it.Node.Content ?? new())
                {
                    var id = BlobId.Parse(hex);
                    var loc = Locate(id);
                    mine.Add((id, loc, off));
                    off += loc.UncompressedLength > 0 ? loc.UncompressedLength : loc.Length - ResticCrypto.Overhead;
                }
            }
            catch (Exception ex)
            {
                job.Failed = true;
                res.Failed++;
                res.Error(it.RelativePath + ": " + ex.Message);
                onBytes?.Invoke(it.Node.Size);
                continue;
            }
            foreach (var (id, loc, o) in mine)
            {
                locs[id] = loc;
                if (!targets.TryGetValue(id, out var list)) targets[id] = list = new();
                list.Add((job, o));
            }
            job.Remaining = mine.Count;
            job.Size = off;
        }

        void Finish(FileJob j)
        {
            lock (j.Lock)
            {
                j.Handle?.Dispose();
                j.Handle = null;
            }
            File.Move(j.Part, j.Local, true);
            if (j.Item.Node.Mtime is { } mt) try { File.SetLastWriteTimeUtc(j.Local, mt.UtcDateTime); } catch { }
            Interlocked.Increment(ref res.Files);
            Interlocked.Add(ref res.Bytes, j.Size);
        }

        void Fail(FileJob j, string msg)
        {
            lock (j.Lock)
            {
                if (j.Failed) return;
                j.Failed = true;
                j.Handle?.Dispose();
                j.Handle = null;
            }
            try { File.Delete(j.Part); } catch { }
            Interlocked.Increment(ref res.Failed);
            res.Error(j.Item.RelativePath + ": " + msg);
            onBytes?.Invoke(Math.Max(0, j.Item.Node.Size - Interlocked.Read(ref j.Written)));
        }

        // Tạo file tạm đúng kích thước; file rỗng xong luôn.
        foreach (var j in jobs.Where(j => !j.Failed))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(j.Local)!);
                using (File.OpenHandle(j.Part, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.None, j.Size)) { }
                if (j.Remaining == 0) Finish(j);
            }
            catch (Exception ex) { Fail(j, ex.Message); }
        }

        // Gom theo pack → các đoạn liền nhau (cho phép khe nhỏ để bớt request).
        var ranges = new List<PackRange>();
        foreach (var g in locs.GroupBy(kv => kv.Value.Pack).OrderBy(g => g.Key))
        {
            PackRange? cur = null;
            foreach (var kv in g.OrderBy(kv => kv.Value.Offset))
            {
                var loc = kv.Value;
                if (cur != null && loc.Offset - cur.End <= MaxGap && loc.Offset + loc.Length - cur.Offset <= MaxRange)
                {
                    cur.Blobs.Add((kv.Key, loc));
                    cur.End = Math.Max(cur.End, loc.Offset + loc.Length);
                }
                else
                {
                    cur = new PackRange(loc.Pack, loc.Offset) { End = loc.Offset + loc.Length };
                    cur.Blobs.Add((kv.Key, loc));
                    ranges.Add(cur);
                }
            }
        }

        try
        {
            await Parallel.ForEachAsync(ranges, new ParallelOptions { MaxDegreeOfParallelism = parallel, CancellationToken = ct }, async (r, c) =>
            {
                // Mọi file cần đoạn này đã lỗi → khỏi tải.
                if (r.Blobs.All(b => targets[b.Id].All(t => t.Job.Failed))) return;
                byte[] raw;
                try { raw = await Backend.ReadAsync(PackPath(r.Pack), r.Offset, (int)(r.End - r.Offset), c); }
                catch (Exception ex) when (!c.IsCancellationRequested)
                {
                    foreach (var b in r.Blobs) foreach (var t in targets[b.Id]) Fail(t.Job, "không tải được pack " + _packs[r.Pack][..8] + ": " + S3ClientFactory.Describe(ex));
                    return;
                }
                foreach (var (id, loc) in r.Blobs)
                {
                    byte[] plain;
                    try { plain = DecodeBlob(id, loc, raw.AsSpan((int)(loc.Offset - r.Offset), loc.Length)); }
                    catch (Exception ex)
                    {
                        foreach (var t in targets[id]) Fail(t.Job, ex.Message);
                        continue;
                    }
                    foreach (var (job, off) in targets[id])
                    {
                        bool done;
                        try
                        {
                            lock (job.Lock)
                            {
                                if (job.Failed) continue;
                                job.Handle ??= File.OpenHandle(job.Part, FileMode.Open, FileAccess.Write, FileShare.None);
                                RandomAccess.Write(job.Handle, plain, off);
                                job.Written += plain.Length;
                                done = --job.Remaining == 0;
                            }
                        }
                        catch (Exception ex) { Fail(job, ex.Message); continue; }
                        onBytes?.Invoke(plain.Length);
                        if (done)
                        {
                            try { Finish(job); } catch (Exception ex) { Fail(job, ex.Message); }
                        }
                    }
                }
            });
        }
        finally
        {
            // Hủy giữa chừng / lỗi: đóng file, xóa các file tạm chưa xong.
            foreach (var j in jobs)
            {
                lock (j.Lock) { j.Handle?.Dispose(); j.Handle = null; }
                if (j.Remaining > 0 && !j.Failed) try { File.Delete(j.Part); } catch { }
            }
        }

        foreach (var it in items.Where(i => !i.Node.IsDir && !i.Node.IsFile))
        {
            res.Skipped++;
            res.Error(it.RelativePath + ": bỏ qua " + (it.Node.Type == "symlink" ? "symlink → " + it.Node.LinkTarget : it.Node.Type));
        }
        // Giờ sửa của thư mục đặt sau cùng (ghi file vào làm đổi giờ của thư mục), thư mục sâu trước.
        foreach (var it in items.Where(i => i.Node.IsDir).OrderByDescending(i => i.RelativePath.Length))
            if (it.Node.Mtime is { } mt) try { Directory.SetLastWriteTimeUtc(LocalPath(targetDir, it.RelativePath), mt.UtcDateTime); } catch { }
        return res;
    }

    private sealed class PackRange(int pack, long offset)
    {
        public int Pack { get; } = pack;
        public long Offset { get; } = offset;
        public long End { get; set; }
        public List<(BlobId Id, BlobLocation Loc)> Blobs { get; } = new();
    }

    /// <summary>Thời điểm RFC3339 của restic (phần lẻ tới nano giây — .NET chỉ nhận 7 chữ số).</summary>
    public static DateTimeOffset? ParseTime(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        int dot = s.IndexOf('.', 19);
        if (dot > 0)
        {
            int end = dot + 1;
            while (end < s.Length && char.IsDigit(s[end])) end++;
            string frac = s[(dot + 1)..end];
            if (frac.Length > 7) s = s[..(dot + 1)] + frac[..7] + s[end..];
        }
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
    }

    public static bool LooksLikeRepo(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        return set.Contains("config") && set.Contains("keys/") && (set.Contains("snapshots/") || set.Contains("data/") || set.Contains("index/"));
    }
}
