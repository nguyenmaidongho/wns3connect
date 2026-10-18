using Amazon.S3;
using Amazon.S3.Model;

namespace WNS3Connect.Services.Restic;

/// <summary>Nơi chứa kho restic: chỉ cần liệt kê thư mục và đọc (một đoạn) file.</summary>
public interface IResticBackend
{
    string Describe { get; }
    Task<List<(string Name, long Size)>> ListAsync(string dir, CancellationToken ct);
    Task<byte[]> ReadAsync(string path, long offset, int length, CancellationToken ct);
    Task<byte[]> ReadAllAsync(string path, CancellationToken ct);
}

/// <summary>Kho restic nằm trên S3 (restic -r s3:... hoặc rclone:remote:bucket/... của WebNowBackup).</summary>
public sealed class S3ResticBackend(AmazonS3Client client, string bucket, string prefix) : IResticBackend
{
    private readonly string _prefix = prefix.Length == 0 || prefix.EndsWith('/') ? prefix : prefix + "/";
    public string Bucket => bucket;
    public string Prefix => _prefix;
    public string Describe => $"{bucket}/{_prefix}";

    public async Task<List<(string Name, long Size)>> ListAsync(string dir, CancellationToken ct)
    {
        var res = new List<(string, long)>();
        string p = _prefix + dir.TrimEnd('/') + "/";
        string? token = null;
        do
        {
            // Không dùng Delimiter: data/ có 256 thư mục con, liệt kê một lượt nhanh hơn.
            var r = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket, Prefix = p, ContinuationToken = token, MaxKeys = 1000 }, ct);
            foreach (var o in r.S3Objects ?? new())
            {
                if (o.Key.EndsWith('/')) continue;
                int i = o.Key.LastIndexOf('/');
                res.Add((o.Key[(i + 1)..], o.Size ?? 0));
            }
            token = r.IsTruncated == true ? r.NextContinuationToken : null;
        } while (token != null);
        return res;
    }

    public async Task<byte[]> ReadAsync(string path, long offset, int length, CancellationToken ct)
    {
        var req = new GetObjectRequest { BucketName = bucket, Key = _prefix + path, ByteRange = new ByteRange(offset, offset + length - 1) };
        using var r = await client.GetObjectAsync(req, ct);
        var buf = new byte[length];
        await using var s = r.ResponseStream;
        int read = 0;
        while (read < length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new IOException($"Đọc thiếu dữ liệu từ {path} ({read}/{length} byte).");
            read += n;
        }
        return buf;
    }

    public async Task<byte[]> ReadAllAsync(string path, CancellationToken ct)
    {
        using var r = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = _prefix + path }, ct);
        using var ms = new MemoryStream(r.ContentLength > 0 ? (int)r.ContentLength : 4096);
        await r.ResponseStream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}

/// <summary>Kho restic ở thư mục trên máy (dùng cho test, hoặc kho đã tải về).</summary>
public sealed class LocalResticBackend(string root) : IResticBackend
{
    public string Describe => root;

    public Task<List<(string Name, long Size)>> ListAsync(string dir, CancellationToken ct)
    {
        string d = Path.Combine(root, dir);
        var res = new List<(string, long)>();
        if (Directory.Exists(d))
            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                res.Add((Path.GetFileName(f), new FileInfo(f).Length));
        return Task.FromResult(res);
    }

    public async Task<byte[]> ReadAsync(string path, long offset, int length, CancellationToken ct)
    {
        await using var fs = new FileStream(Path.Combine(root, path), FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Position = offset;
        var buf = new byte[length];
        await fs.ReadExactlyAsync(buf, ct);
        return buf;
    }

    public Task<byte[]> ReadAllAsync(string path, CancellationToken ct) => File.ReadAllBytesAsync(Path.Combine(root, path), ct);
}
