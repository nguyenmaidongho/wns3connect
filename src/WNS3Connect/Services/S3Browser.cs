using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;
using WNS3Connect.Models;

namespace WNS3Connect.Services;

/// <summary>
/// Danh sách của một "thư mục" (bucket + prefix), được nạp dần theo từng trang 1000 mục và giữ lại trong cache.
/// Nhờ vậy: mở thư mục chỉ cần chờ trang đầu, quay lại thư mục cũ hiện ngay, thư mục đã "liếc qua" (prefetch) mở tức thì.
/// </summary>
public sealed class FolderListing
{
    public string Bucket { get; }
    public string Prefix { get; }
    public List<S3Entry> Items { get; } = new();
    public string? NextToken { get; set; }
    public bool Started { get; set; }
    public bool Complete { get; set; }
    public DateTime FetchedAtUtc { get; set; } = DateTime.MinValue;
    public int FolderCount { get; set; }
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
    internal SemaphoreSlim Gate { get; } = new(1, 1);

    public FolderListing(string bucket, string prefix) { Bucket = bucket; Prefix = prefix; }
}

public sealed class S3Browser : IDisposable
{
    public const int PageSize = 1000;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(45);

    private readonly ConcurrentDictionary<string, FolderListing> _cache = new();
    private readonly SemaphoreSlim _prefetchSlots = new(3, 3);

    public S3Account Account { get; }
    public AmazonS3Client Client { get; }

    public S3Browser(S3Account account)
    {
        Account = account;
        Client = S3ClientFactory.Create(account);
    }

    private static string CacheKey(string bucket, string prefix) => bucket + "\n" + prefix;

    public async Task<List<string>> ListBucketsAsync(CancellationToken ct)
    {
        var r = await Client.ListBucketsAsync(new ListBucketsRequest(), ct);
        return (r.Buckets ?? new()).Select(b => b.BucketName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Listing trong cache (có thể chưa nạp trang nào).</summary>
    public FolderListing Get(string bucket, string prefix) => _cache.GetOrAdd(CacheKey(bucket, prefix), _ => new FolderListing(bucket, prefix));

    public bool TryGet(string bucket, string prefix, out FolderListing listing) => _cache.TryGetValue(CacheKey(bucket, prefix), out listing!);

    public static bool IsStale(FolderListing l) => DateTime.UtcNow - l.FetchedAtUtc > StaleAfter;

    /// <summary>Tạo listing mới thay cho bản trong cache (dùng khi Refresh: bản cũ vẫn hiển thị cho tới khi trang đầu mới về).</summary>
    public FolderListing Fresh(string bucket, string prefix)
    {
        var l = new FolderListing(bucket, prefix);
        _cache[CacheKey(bucket, prefix)] = l;
        return l;
    }

    public void Invalidate(string bucket, string prefix) => _cache.TryRemove(CacheKey(bucket, prefix), out _);

    public void InvalidateBucket(string bucket)
    {
        foreach (var k in _cache.Keys.Where(k => k.StartsWith(bucket + "\n", StringComparison.Ordinal)).ToList())
            _cache.TryRemove(k, out _);
    }

    /// <summary>Nạp thêm một trang vào listing. Trả về các mục mới (rỗng nếu đã hết).</summary>
    public async Task<IReadOnlyList<S3Entry>> FetchNextPageAsync(FolderListing l, CancellationToken ct)
    {
        await l.Gate.WaitAsync(ct);
        try
        {
            if (l.Complete) return Array.Empty<S3Entry>();
            var req = new ListObjectsV2Request
            {
                BucketName = l.Bucket,
                Prefix = l.Prefix.Length > 0 ? l.Prefix : null,
                Delimiter = "/",
                MaxKeys = PageSize,
                ContinuationToken = l.NextToken
            };
            var r = await Client.ListObjectsV2Async(req, ct);

            var page = new List<S3Entry>((r.CommonPrefixes?.Count ?? 0) + (r.S3Objects?.Count ?? 0));
            foreach (var p in r.CommonPrefixes ?? new())
            {
                if (string.IsNullOrEmpty(p) || p == l.Prefix) continue;
                page.Add(new S3Entry { Key = p, IsFolder = true });
            }
            foreach (var o in r.S3Objects ?? new())
            {
                // Object "thư mục rỗng" (key = prefix hiện tại, kết thúc bằng "/") không hiện.
                if (o.Key == l.Prefix) continue;
                page.Add(new S3Entry
                {
                    Key = o.Key,
                    Size = o.Size ?? 0,
                    LastModified = o.LastModified,
                    StorageClass = o.StorageClass?.Value ?? "",
                    ETag = o.ETag ?? ""
                });
            }

            l.Items.AddRange(page);
            foreach (var e in page)
            {
                if (e.IsFolder) l.FolderCount++;
                else { l.FileCount++; l.TotalBytes += e.Size; }
            }
            l.NextToken = r.NextContinuationToken;
            l.Complete = r.IsTruncated != true || string.IsNullOrEmpty(r.NextContinuationToken);
            if (!l.Started) l.FetchedAtUtc = DateTime.UtcNow;
            l.Started = true;
            return page;
        }
        finally { l.Gate.Release(); }
    }

    /// <summary>Nạp sẵn trang đầu của một thư mục (khi rê chuột vào) để bấm vào là hiện ngay.</summary>
    public async Task PrefetchAsync(string bucket, string prefix)
    {
        var l = Get(bucket, prefix);
        if (l.Started && !IsStale(l)) return;
        if (!await _prefetchSlots.WaitAsync(0)) return;
        try
        {
            if (l.Started) l = Fresh(bucket, prefix);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await FetchNextPageAsync(l, cts.Token);
        }
        catch { Invalidate(bucket, prefix); }
        finally { _prefetchSlots.Release(); }
    }

    /// <summary>Liệt kê đệ quy mọi object dưới prefix (để tải/xóa cả thư mục).</summary>
    public async IAsyncEnumerable<S3Entry> ListAllAsync(string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        do
        {
            var r = await Client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = prefix.Length > 0 ? prefix : null,
                MaxKeys = PageSize,
                ContinuationToken = token
            }, ct);
            foreach (var o in r.S3Objects ?? new())
                yield return new S3Entry { Key = o.Key, Size = o.Size ?? 0, LastModified = o.LastModified, StorageClass = o.StorageClass?.Value ?? "", ETag = o.ETag ?? "" };
            token = r.IsTruncated == true ? r.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(token));
    }

    // SDK v4 không gửi Content-MD5 cho DeleteObjects; Ceph/CloudFly... trả 400 -> chuyển sang xóa từng key.
    private bool _batchDeleteUnsupported;

    /// <summary>Lỗi xóa gần nhất (để ghi Event log).</summary>
    public string? LastDeleteError { get; private set; }

    /// <summary>Xóa nhiều key (mỗi lượt tối đa 1000). Trả về số key xóa lỗi.</summary>
    public Task<int> DeleteKeysAsync(string bucket, IReadOnlyList<string> keys, Action<int>? progress, CancellationToken ct) =>
        DeleteObjectsCoreAsync(bucket, keys.Select(k => new KeyVersion { Key = k }).ToList(), progress, ct);

    /// <summary>Xóa nhiều key (kèm VersionId nếu có: xóa vĩnh viễn đúng phiên bản đó). Lô 1000; server không nhận DeleteObjects thì xóa từng cái.</summary>
    private async Task<int> DeleteObjectsCoreAsync(string bucket, IReadOnlyList<KeyVersion> items, Action<int>? progress, CancellationToken ct)
    {
        int failed = 0, done = 0;
        LastDeleteError = null;
        for (int i = 0; i < items.Count; i += 1000)
        {
            var chunk = items.Skip(i).Take(1000).ToList();
            if (!_batchDeleteUnsupported)
            {
                try
                {
                    var r = await Client.DeleteObjectsAsync(new DeleteObjectsRequest { BucketName = bucket, Objects = chunk, Quiet = true }, ct);
                    if (r.DeleteErrors is { Count: > 0 } errs) { failed += errs.Count; LastDeleteError = $"{errs[0].Key}: {errs[0].Code} {errs[0].Message}"; }
                    done += chunk.Count;
                    progress?.Invoke(done);
                    continue;
                }
                catch (DeleteObjectsException ex)
                {
                    int n = ex.Response?.DeleteErrors?.Count ?? chunk.Count;
                    failed += n;
                    LastDeleteError = S3ClientFactory.Describe(ex);
                    done += chunk.Count;
                    progress?.Invoke(done);
                    continue;
                }
                catch (AmazonS3Exception ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.NotImplemented or System.Net.HttpStatusCode.MethodNotAllowed)
                {
                    _batchDeleteUnsupported = true;
                }
            }

            // Xóa từng key, 16 request song song.
            int baseDone = done;
            int local = 0;
            await Parallel.ForEachAsync(chunk, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct }, async (k, t) =>
            {
                try { await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = k.Key, VersionId = k.VersionId }, t); }
                catch (AmazonS3Exception ex) when (ex.ErrorCode is not ("NoSuchKey" or "NoSuchVersion"))
                {
                    Interlocked.Increment(ref failed);
                    LastDeleteError = $"{k.Key}: {S3ClientFactory.Describe(ex)}";
                }
                int n = Interlocked.Increment(ref local);
                if (n % 50 == 0) progress?.Invoke(baseDone + n);
            });
            done += chunk.Count;
            progress?.Invoke(done);
        }
        return failed;
    }

    /// <summary>
    /// Bucket có (hoặc từng có) versioning: xóa thường chỉ đặt "dấu xóa", bản cũ vẫn chiếm dung lượng và thư mục vẫn hiện.
    /// Hàm này xóa VĨNH VIỄN mọi phiên bản + dấu xóa dưới prefix (keyFilter: chỉ các key được chọn). Không hỗ trợ versioning → trả (0, 0).
    /// </summary>
    public async Task<(long total, int failed)> PurgeVersionsAsync(string bucket, string prefix, Func<string, bool>? keyFilter, Action<long>? onPurged, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<List<KeyVersion>>(ParallelDeleteBatches * 2);
        long found = 0, done = 0;
        int failed = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                string? keyMarker = null, versionMarker = null;
                while (true)
                {
                    ListVersionsResponse r;
                    try
                    {
                        r = await Client.ListVersionsAsync(new ListVersionsRequest
                        {
                            BucketName = bucket, Prefix = prefix.Length > 0 ? prefix : null, KeyMarker = keyMarker, VersionIdMarker = versionMarker, MaxKeys = PageSize
                        }, ct);
                    }
                    catch (AmazonS3Exception ex) when (keyMarker == null && ex.StatusCode is System.Net.HttpStatusCode.NotImplemented or System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.MethodNotAllowed)
                    {
                        break; // server không hỗ trợ ListObjectVersions
                    }
                    var page = (r.Versions ?? new())
                        .Where(v => keyFilter == null || keyFilter(v.Key))
                        .Select(v => new KeyVersion { Key = v.Key, VersionId = string.IsNullOrEmpty(v.VersionId) ? null : v.VersionId })
                        .ToList();
                    if (page.Count > 0)
                    {
                        Interlocked.Add(ref found, page.Count);
                        await channel.Writer.WriteAsync(page, ct);
                    }
                    if (r.IsTruncated != true) break;
                    keyMarker = r.NextKeyMarker;
                    versionMarker = r.NextVersionIdMarker;
                    if (string.IsNullOrEmpty(keyMarker)) break;
                }
                channel.Writer.Complete();
            }
            catch (Exception ex) { channel.Writer.Complete(ex); throw; }
        }, ct);

        var consumers = Enumerable.Range(0, ParallelDeleteBatches).Select(_ => Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct))
            {
                Interlocked.Add(ref failed, await DeleteObjectsCoreAsync(bucket, batch, null, ct));
                onPurged?.Invoke(Interlocked.Add(ref done, batch.Count));
            }
        }, ct)).ToList();

        await Task.WhenAll(consumers.Append(producer));
        return (Interlocked.Read(ref found), failed);
    }

    /// <summary>Số lô DeleteObjects (1000 key) gửi song song.</summary>
    public const int ParallelDeleteBatches = 4;

    /// <summary>Xóa một danh sách key: chia lô 1000, nhiều lô chạy song song. onDeleted nhận tổng số key đã xử lý.</summary>
    public async Task<int> DeleteManyAsync(string bucket, IReadOnlyList<string> keys, Action<long>? onDeleted, CancellationToken ct)
    {
        int failed = 0;
        long done = 0;
        var chunks = keys.Chunk(1000).ToList();
        await Parallel.ForEachAsync(chunks, new ParallelOptions { MaxDegreeOfParallelism = ParallelDeleteBatches, CancellationToken = ct }, async (chunk, t) =>
        {
            Interlocked.Add(ref failed, await DeleteKeysAsync(bucket, chunk, null, t));
            onDeleted?.Invoke(Interlocked.Add(ref done, chunk.Length));
        });
        return failed;
    }

    /// <summary>
    /// Xóa cả "thư mục": liệt kê từng trang 1000 key và đẩy ngay sang xóa (không chờ liệt kê hết),
    /// nhiều lô DeleteObjects chạy song song. An toàn vì ListObjectsV2 phân trang theo key cuối, xóa key trước đó không làm lệch trang.
    /// </summary>
    public async Task<(long total, int failed)> DeletePrefixAsync(string bucket, string prefix, Action<long>? onListed, Action<long>? onDeleted, CancellationToken ct)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<List<string>>(ParallelDeleteBatches * 2);
        long listed = 0, done = 0;
        int failed = 0;

        var producer = Task.Run(async () =>
        {
            try
            {
                string? token = null;
                do
                {
                    var r = await Client.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = bucket, Prefix = prefix.Length > 0 ? prefix : null, MaxKeys = PageSize, ContinuationToken = token
                    }, ct);
                    var page = (r.S3Objects ?? new()).Select(o => o.Key).ToList();
                    if (page.Count > 0)
                    {
                        onListed?.Invoke(Interlocked.Add(ref listed, page.Count));
                        await channel.Writer.WriteAsync(page, ct);
                    }
                    token = r.IsTruncated == true ? r.NextContinuationToken : null;
                } while (!string.IsNullOrEmpty(token));
                channel.Writer.Complete();
            }
            catch (Exception ex) { channel.Writer.Complete(ex); throw; }
        }, ct);

        var consumers = Enumerable.Range(0, ParallelDeleteBatches).Select(_ => Task.Run(async () =>
        {
            await foreach (var batch in channel.Reader.ReadAllAsync(ct))
            {
                Interlocked.Add(ref failed, await DeleteKeysAsync(bucket, batch, null, ct));
                onDeleted?.Invoke(Interlocked.Add(ref done, batch.Count));
            }
        }, ct)).ToList();

        await Task.WhenAll(consumers.Append(producer));

        // Object "thư mục rỗng" (key = prefix) nếu có.
        if (prefix.EndsWith('/'))
        {
            try { await Client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = bucket, Key = prefix }, ct); } catch (AmazonS3Exception) { }
        }
        return (Interlocked.Read(ref listed), failed);
    }

    public async Task CreateFolderAsync(string bucket, string key, CancellationToken ct)
    {
        if (!key.EndsWith('/')) key += "/";
        await Client.PutObjectAsync(new PutObjectRequest { BucketName = bucket, Key = key, ContentBody = "" }, ct);
    }

    public Task<GetObjectMetadataResponse> HeadAsync(string bucket, string key, CancellationToken ct) =>
        Client.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = bucket, Key = key }, ct);

    public Task<string> PresignAsync(string bucket, string key, TimeSpan validFor) =>
        Client.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(validFor),
            Protocol = Account.UseSsl ? Protocol.HTTPS : Protocol.HTTP
        });

    public void Dispose() => Client.Dispose();
}
