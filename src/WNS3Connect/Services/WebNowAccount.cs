using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WNS3Connect.Models;

namespace WNS3Connect.Services;

public class WebNowException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Tài khoản S3 trong vault (Secret Key để dạng rõ — cả vault đã mã hóa đầu-cuối).</summary>
public class VaultS3Account
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "us-east-1";
    [JsonPropertyName("accessKey")] public string AccessKey { get; set; } = "";
    [JsonPropertyName("secretKey")] public string? SecretKey { get; set; }
    [JsonPropertyName("useSsl")] public bool UseSsl { get; set; } = true;
    [JsonPropertyName("pathStyle")] public bool PathStyle { get; set; } = true;
    [JsonPropertyName("externalBuckets")] public List<string> ExternalBuckets { get; set; } = new();
    /// <summary>Mật khẩu kho restic dạng rõ trong vault. Null khi chưa có kho nào (giữ nguyên JSON cũ → không đẩy bản mới vô cớ).</summary>
    [JsonPropertyName("resticRepos")] public List<VaultResticRepo>? ResticRepos { get; set; }
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; }
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; }

    public static VaultS3Account From(S3Account a) => new()
    {
        Id = a.Id, Name = a.Name, Endpoint = a.Endpoint, Region = a.Region, AccessKey = a.AccessKey,
        SecretKey = SecretProtector.Decrypt(a.SecretKeyEnc), UseSsl = a.UseSsl, PathStyle = a.PathStyle,
        ExternalBuckets = new(a.ExternalBuckets), CreatedAt = a.CreatedAt, UpdatedAt = a.UpdatedAt,
        ResticRepos = a.ResticRepos.Count == 0 ? null
            : a.ResticRepos.Select(r => new VaultResticRepo { Bucket = r.Bucket, Path = r.Path, Password = SecretProtector.Decrypt(r.PasswordEnc), SavedAt = r.SavedAt }).ToList()
    };

    public S3Account ToLocal() => new()
    {
        Id = Id, Name = Name, Endpoint = Endpoint, Region = Region, AccessKey = AccessKey,
        SecretKeyEnc = SecretProtector.Encrypt(SecretKey), UseSsl = UseSsl, PathStyle = PathStyle,
        ExternalBuckets = new(ExternalBuckets ?? new()), CreatedAt = CreatedAt, UpdatedAt = UpdatedAt,
        ResticRepos = (ResticRepos ?? new()).Select(r => new ResticRepoRef { Bucket = r.Bucket, Path = r.Path, PasswordEnc = SecretProtector.Encrypt(r.Password), SavedAt = r.SavedAt }).ToList()
    };

    [JsonIgnore] public string Fingerprint => JsonSerializer.Serialize(this);
}

public class VaultResticRepo
{
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("savedAt")] public DateTime SavedAt { get; set; }
}

public record S3MergeResult(List<VaultS3Account> Accounts, List<S3AccountTombstone> Tombstones, bool LocalChanged, int Added, int Updated, int Deleted);

public record WebNowSyncResult(int Added, int Updated, int Deleted, int Total, int ServerVersion, bool Pushed);

/// <summary>
/// Đăng nhập tài khoản WebNow (cùng tài khoản WNTerm) và đồng bộ danh sách tài khoản S3, mã hóa đầu-cuối bằng cùng khóa vault.
/// Giao thức mã hóa giống hệt WNTerm (AccountService.cs / wn-crypto.js): PBKDF2-SHA256 → HKDF auth/enc, vault AES-256-GCM.
/// Dữ liệu nằm trong ngăn riêng /appvault?app=wns3connect — không bao giờ ghi vào vault của WNTerm.
/// </summary>
public class WebNowAccount
{
    public const string DefaultBaseUrl = "https://wnterm.webnow.vn/api/v1";
    public const string SignUpUrl = "https://wnterm.webnow.vn/dang-ky";
    private const int NewKdfIterations = 600000;
    private const int TombstoneDays = 180;

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly AppSettings _settings;
    private readonly AccountStore _store;
    private readonly SemaphoreSlim _syncGate = new(1, 1);

    public WebNowAccount(AppSettings settings, AccountStore store)
    {
        _settings = settings;
        _store = store;
    }

    private string BaseUrl => string.IsNullOrWhiteSpace(_settings.AccountApiUrl)
        ? Environment.GetEnvironmentVariable("WNS3_ACCOUNT_API") ?? DefaultBaseUrl
        : _settings.AccountApiUrl.TrimEnd('/');

    public bool IsLoggedIn => !string.IsNullOrEmpty(_settings.AccountTokenEnc) && !string.IsNullOrEmpty(_settings.AccountVaultKeyEnc);
    public string Email => _settings.AccountEmail ?? "";
    public DateTime? LastSyncUtc => _settings.AccountLastSyncUtc;
    public bool FromWNTerm => _settings.AccountTokenFromWNTerm;

    private static string DeviceName() => Environment.MachineName + " (WNS3Connect)";

    // ===== Mã hóa (khớp WNTerm) =====

    private static string NormEmail(string email) => email.Trim().ToLowerInvariant();

    private static (byte[] auth, byte[] enc) Derive(string password, string email, int iterations)
    {
        byte[] master = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)),
            Encoding.UTF8.GetBytes("wnterm:v1:" + NormEmail(email)), iterations, HashAlgorithmName.SHA256, 32);
        try
        {
            return (HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, Array.Empty<byte>(), Encoding.UTF8.GetBytes("wnterm-auth")),
                    HKDF.DeriveKey(HashAlgorithmName.SHA256, master, 32, Array.Empty<byte>(), Encoding.UTF8.GetBytes("wnterm-enc")));
        }
        finally { CryptographicOperations.ZeroMemory(master); }
    }

    /// <summary>iv(12) | ciphertext | tag(16)</summary>
    internal static byte[] Seal(byte[] key, byte[] plain)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(12);
        byte[] ct = new byte[plain.Length];
        byte[] tag = new byte[16];
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(iv, plain, ct, tag);
        return [.. iv, .. ct, .. tag];
    }

    internal static byte[]? Open(byte[] key, byte[] raw)
    {
        if (raw.Length < 28) return null;
        try
        {
            byte[] pt = new byte[raw.Length - 28];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(raw.AsSpan(0, 12), raw.AsSpan(12, pt.Length), raw.AsSpan(raw.Length - 16), pt);
            return pt;
        }
        catch (CryptographicException) { return null; }
    }

    // ===== HTTP =====

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        int attempts = method == HttpMethod.Get ? 3 : 1;
        for (int i = 1; ; i++)
        {
            try { return await CallOnceAsync(method, path, body, auth, ct); }
            catch (WebNowException ex) when (ex.Code is "network" or "timeout" && i < attempts)
            {
                await Task.Delay(1000 * i, ct);
            }
        }
    }

    private async Task<JsonElement> CallOnceAsync(HttpMethod method, string path, object? body, bool auth, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, BaseUrl + path);
        if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (auth)
        {
            string? token = SecretProtector.Decrypt(_settings.AccountTokenEnc);
            if (string.IsNullOrEmpty(token)) throw new WebNowException(401, "not_logged_in", "Chưa đăng nhập.");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage res;
        try { res = await Http.SendAsync(req, timeout.Token); }
        catch (HttpRequestException ex) { throw new WebNowException(0, "network", "Không kết nối được máy chủ: " + ex.Message); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new WebNowException(0, "timeout", "Máy chủ phản hồi quá lâu, hãy thử lại."); }
        using (res)
        {
            string text = await res.Content.ReadAsStringAsync(timeout.Token);
            JsonElement root;
            try { root = JsonDocument.Parse(text).RootElement.Clone(); }
            catch (JsonException) { throw new WebNowException((int)res.StatusCode, "bad_response", $"Máy chủ trả về dữ liệu lạ ({(int)res.StatusCode})."); }
            if (!res.IsSuccessStatusCode)
            {
                string code = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "HTTP " + (int)res.StatusCode;
                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && auth)
                {
                    ClearLocal();
                    msg = "Phiên đăng nhập đã hết hạn — hãy đăng nhập lại.";
                }
                throw new WebNowException((int)res.StatusCode, code, msg);
            }
            return root;
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out int i) ? i : 0;

    private byte[] VaultKey()
    {
        string? b = SecretProtector.Decrypt(_settings.AccountVaultKeyEnc);
        if (string.IsNullOrEmpty(b)) throw new WebNowException(401, "not_logged_in", "Chưa đăng nhập.");
        return Convert.FromBase64String(b);
    }

    private void SaveLogin(string email, string token, byte[] vaultKey, bool fromWNTerm)
    {
        _settings.AccountEmail = NormEmail(email);
        _settings.AccountTokenEnc = SecretProtector.Encrypt(token);
        _settings.AccountVaultKeyEnc = SecretProtector.Encrypt(Convert.ToBase64String(vaultKey));
        _settings.AccountTokenFromWNTerm = fromWNTerm;
        _settings.AccountVaultVersion = 0;
        SettingsStore.Save(_settings);
    }

    private void ClearLocal()
    {
        _settings.AccountTokenEnc = null;
        _settings.AccountVaultKeyEnc = null;
        _settings.AccountTokenFromWNTerm = false;
        _settings.AccountVaultVersion = 0;
        SettingsStore.Save(_settings);
    }

    // ===== Đăng nhập =====

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var pre = await CallAsync(HttpMethod.Post, "/prelogin", new { email = NormEmail(email) }, false, ct);
        int iter = Int(pre, "kdfIterations");
        if (iter < 200000) iter = NewKdfIterations;

        var (auth, enc) = await Task.Run(() => Derive(password, email, iter), ct);
        var root = await CallAsync(HttpMethod.Post, "/login", new
        {
            email = NormEmail(email),
            authKey = Convert.ToBase64String(auth),
            deviceName = DeviceName()
        }, false, ct);

        byte[]? vaultKey = Open(enc, Convert.FromBase64String(Str(root, "wrappedPw")));
        if (vaultKey == null) throw new WebNowException(0, "bad_key", "Không mở được khóa dữ liệu của tài khoản.");
        SaveLogin(email, Str(root, "token"), vaultKey, fromWNTerm: false);
    }

    /// <summary>Phiên đăng nhập của WNTerm trên máy này (cùng user Windows), nếu có.</summary>
    public static (string email, string token, byte[] vaultKey, string apiUrl)? ReadWNTermLogin()
    {
        try
        {
            string file = Path.Combine(AppPaths.WNTermDataDir, "settings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string Get(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string email = Get("AccountEmail");
            string? token = SecretProtector.Decrypt(Get("AccountTokenEnc"), SecretProtector.WNTermEntropy);
            string? key = SecretProtector.Decrypt(Get("AccountVaultKeyEnc"), SecretProtector.WNTermEntropy);
            if (email.Length == 0 || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(key)) return null;
            return (email, token, Convert.FromBase64String(key), Get("AccountApiUrl"));
        }
        catch { return null; }
    }

    /// <summary>Dùng luôn phiên đăng nhập WNTerm trên máy này (không phải gõ mật khẩu). Kiểm tra token còn sống bằng /me.</summary>
    public async Task UseWNTermLoginAsync(CancellationToken ct = default)
    {
        var w = ReadWNTermLogin() ?? throw new WebNowException(0, "no_wnterm", "WNTerm trên máy này chưa đăng nhập.");
        SaveLogin(w.email, w.token, w.vaultKey, fromWNTerm: true);
        if (!string.IsNullOrWhiteSpace(w.apiUrl)) { _settings.AccountApiUrl = w.apiUrl; SettingsStore.Save(_settings); }
        await CallAsync(HttpMethod.Get, "/me", null, true, ct);
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        // Token mượn của WNTerm: chỉ quên ở đây, không gọi /logout (sẽ làm WNTerm bị đăng xuất theo).
        if (!_settings.AccountTokenFromWNTerm)
        {
            try { await CallAsync(HttpMethod.Post, "/logout", null, true, ct); } catch { }
        }
        ClearLocal();
    }

    // ===== Đồng bộ =====

    /// <summary>Gộp theo Id: sự kiện mới nhất thắng (sửa hoặc xóa); hòa: máy này &gt; máy chủ &gt; dấu xóa.</summary>
    public static S3MergeResult Merge(IReadOnlyList<VaultS3Account> local, IReadOnlyList<S3AccountTombstone> localTombs,
        IReadOnlyList<VaultS3Account> remote, IReadOnlyList<S3AccountTombstone> remoteTombs, DateTime nowUtc)
    {
        var l = local.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.UpdatedAt).First());
        var r = remote.Where(a => a.Id != Guid.Empty).GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.UpdatedAt).First());
        var lt = localTombs.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.At).First());
        var rt = remoteTombs.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.At).First());

        var outAcc = new List<VaultS3Account>();
        var outTomb = new List<S3AccountTombstone>();
        int added = 0, updated = 0, deleted = 0;
        bool localChanged = false;

        foreach (var id in l.Keys.Concat(r.Keys).Concat(lt.Keys).Concat(rt.Keys).Distinct().OrderBy(x => x))
        {
            l.TryGetValue(id, out var la);
            r.TryGetValue(id, out var ra);
            var cands = new List<(DateTime at, int rank, char kind)>();
            if (la != null) cands.Add((la.UpdatedAt, 3, 'L'));
            if (ra != null) cands.Add((ra.UpdatedAt, 2, 'R'));
            if (lt.TryGetValue(id, out var ltv)) cands.Add((ltv.At, 1, 'l'));
            if (rt.TryGetValue(id, out var rtv)) cands.Add((rtv.At, 0, 'r'));
            var win = cands.OrderByDescending(c => c.at).ThenByDescending(c => c.rank).First();

            switch (win.kind)
            {
                case 'L':
                    outAcc.Add(la!);
                    if (ltv != null) localChanged = true;
                    break;
                case 'R':
                    outAcc.Add(ra!);
                    if (la == null) added++;
                    else if (la.Fingerprint != ra!.Fingerprint) updated++;
                    if (la == null || la.Fingerprint != ra!.Fingerprint || ltv != null) localChanged = true;
                    break;
                default:
                    if (la != null) { deleted++; localChanged = true; }
                    var t = win.kind == 'l' ? ltv! : rtv!;
                    if ((nowUtc - t.At).TotalDays <= TombstoneDays)
                    {
                        outTomb.Add(new S3AccountTombstone { Id = id, At = t.At, Name = t.Name.Length > 0 ? t.Name : (la?.Name ?? ra?.Name ?? "") });
                        if (win.kind == 'r' && ltv == null) localChanged = true;
                    }
                    break;
            }
        }
        return new S3MergeResult(outAcc, outTomb, localChanged, added, updated, deleted);
    }

    private static readonly JsonSerializerOptions VaultJson = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private static List<T> ReadList<T>(JsonObject? root, string name)
    {
        if (root?[name] is not JsonArray arr) return new();
        try { return arr.Deserialize<List<T>>(VaultJson) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Tên ngăn dữ liệu của app này trên máy chủ (/appvault?app=...). Tách hẳn khỏi vault của WNTerm.</summary>
    public const string AppSlot = "wns3connect";

    private JsonObject? OpenBlob(JsonElement res)
    {
        if (!res.TryGetProperty("blob", out var b) || b.ValueKind != JsonValueKind.String) return null;
        byte[]? plain = Open(VaultKey(), Convert.FromBase64String(b.GetString()!));
        if (plain == null) throw new WebNowException(0, "bad_key", "Không giải mã được dữ liệu đồng bộ (sai khóa).");
        return JsonNode.Parse(plain) as JsonObject ?? new JsonObject();
    }

    /// <summary>Ngăn riêng của WNS3Connect.</summary>
    private async Task<(int version, JsonObject? root)> PullAppAsync(CancellationToken ct)
    {
        var res = await CallAsync(HttpMethod.Get, "/appvault?app=" + AppSlot, null, true, ct);
        int ver = Int(res, "version");
        return (ver, ver == 0 ? null : OpenBlob(res));
    }

    /// <summary>
    /// Dữ liệu cũ: bản 1.0/1.1 từng ghi tài khoản S3 vào vault chung của WNTerm. Chỉ đọc (không bao giờ ghi lại vault WNTerm),
    /// dùng một lần khi ngăn riêng còn trống để chuyển sang.
    /// </summary>
    private async Task<(List<VaultS3Account> acc, List<S3AccountTombstone> tombs)> ReadLegacyAsync(CancellationToken ct)
    {
        try
        {
            var res = await CallAsync(HttpMethod.Get, "/vault", null, true, ct);
            if (Int(res, "version") == 0) return (new(), new());
            var root = OpenBlob(res);
            return (ReadList<VaultS3Account>(root, "s3Accounts"), ReadList<S3AccountTombstone>(root, "deletedS3Accounts"));
        }
        catch (WebNowException ex) when (ex.Status != 401) { return (new(), new()); }
    }

    /// <summary>Kéo ngăn riêng, gộp danh sách tài khoản S3 với máy này, ghi máy này, đẩy bản gộp lên ngăn riêng.</summary>
    public async Task<WebNowSyncResult> SyncAsync(CancellationToken ct = default, bool forceNewVersion = false)
    {
        await _syncGate.WaitAsync(ct);
        try
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                var (serverVer, root) = await PullAppAsync(ct);
                var remoteAcc = ReadList<VaultS3Account>(root, "s3Accounts");
                var remoteTomb = ReadList<S3AccountTombstone>(root, "deletedS3Accounts");
                bool migrated = false;
                if (serverVer == 0)
                {
                    var (la, lt) = await ReadLegacyAsync(ct);
                    if (la.Count + lt.Count > 0) { remoteAcc = la; remoteTomb = lt; migrated = true; }
                }

                var localAcc = _store.Accounts.Select(VaultS3Account.From).ToList();
                var m = Merge(localAcc, _store.Deleted, remoteAcc, remoteTomb, DateTime.UtcNow);
                if (m.LocalChanged)
                    _store.ReplaceSynced(m.Accounts.Select(a => a.ToLocal()).ToList(), m.Tombstones);

                string Fp(IEnumerable<VaultS3Account> a, IEnumerable<S3AccountTombstone> t) =>
                    JsonSerializer.Serialize(new { a = a.OrderBy(x => x.Id), t = t.OrderBy(x => x.Id).Select(x => new { x.Id, x.At }) });
                bool remoteDiffers = root == null || migrated || Fp(remoteAcc, remoteTomb) != Fp(m.Accounts, m.Tombstones);

                if (!remoteDiffers && !forceNewVersion)
                {
                    MarkSynced(serverVer);
                    return new WebNowSyncResult(m.Added, m.Updated, m.Deleted, m.Accounts.Count, serverVer, false);
                }
                // Ngăn riêng trống và máy này cũng chưa có gì: không tạo bản rỗng.
                if (serverVer == 0 && m.Accounts.Count == 0 && m.Tombstones.Count == 0 && !forceNewVersion)
                {
                    MarkSynced(0);
                    return new WebNowSyncResult(0, 0, 0, 0, 0, false);
                }

                var doc = new JsonObject
                {
                    ["format"] = 1,
                    ["savedAt"] = DateTime.UtcNow,
                    ["device"] = DeviceName(),
                    ["s3Accounts"] = JsonSerializer.SerializeToNode(m.Accounts.OrderBy(a => a.Id).ToList(), VaultJson),
                    ["deletedS3Accounts"] = JsonSerializer.SerializeToNode(m.Tombstones.OrderBy(t => t.Id).ToList(), VaultJson)
                };
                byte[] blob = Seal(VaultKey(), Encoding.UTF8.GetBytes(doc.ToJsonString()));
                try
                {
                    var res = await CallAsync(HttpMethod.Put, "/appvault", new { app = AppSlot, baseVersion = serverVer, blob = Convert.ToBase64String(blob) }, true, ct);
                    int ver = Int(res, "version");
                    MarkSynced(ver);
                    return new WebNowSyncResult(m.Added, m.Updated, m.Deleted, m.Accounts.Count, ver, true);
                }
                catch (WebNowException ex) when (ex.Status == 409) { /* máy khác vừa ghi: kéo lại rồi gộp */ }
            }
            throw new WebNowException(409, "conflict", "Xung đột đồng bộ, hãy thử lại.");
        }
        catch (WebNowException ex) when (ex.Status == 404 && ex.Code == "not_found")
        {
            throw new WebNowException(404, "not_found", "Máy chủ WebNow chưa hỗ trợ ngăn lưu riêng cho WNS3Connect (cần cập nhật server).");
        }
        finally { _syncGate.Release(); }
    }

    private void MarkSynced(int version)
    {
        _settings.AccountVaultVersion = version;
        _settings.AccountLastSyncUtc = DateTime.UtcNow;
        SettingsStore.Save(_settings);
    }
}
