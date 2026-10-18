// Smoke test các service của WNS3Connect với FakeS3 (http://127.0.0.1:9900).
using System.Diagnostics;
using WNS3Connect.Models;
using WNS3Connect.Services;

if (args.Length > 0 && args[0] == "seed")
{
    var st = AccountStore.Load();
    st.Upsert(new S3Account { Name = args.Length > 1 ? args[1] : "Fake CloudFly (local)", Endpoint = args.Length > 2 ? args[2] : "127.0.0.1:9900", UseSsl = false, PathStyle = true, AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") });
    Console.WriteLine("seeded " + AppPaths.AccountsFile);
    return 0;
}

if (args.Length > 0 && args[0] == "diag")
{
    // CHỈ ĐỌC: chẩn đoán một "thư mục" trên tài khoản thật của người dùng (theo tên tài khoản trong app).
    string accName = args[1], dBucket = args[2], dPrefix = args[3], parent = args.Length > 4 ? args[4] : "";
    var dAcc = AccountStore.Load().Accounts.First(a => a.DisplayName == accName);
    using var c = S3ClientFactory.Create(dAcc);
    try { var v = await c.GetBucketVersioningAsync(new Amazon.S3.Model.GetBucketVersioningRequest { BucketName = dBucket }); Console.WriteLine("versioning=" + (v.VersioningConfig?.Status?.Value ?? "(none)")); }
    catch (Exception ex) { Console.WriteLine("versioning: " + ex.Message); }
    var p1 = await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = dBucket, Prefix = parent, Delimiter = "/", MaxKeys = 50 });
    Console.WriteLine($"parent {parent} delimiter: prefixes=[{string.Join(", ", p1.CommonPrefixes ?? new())}] objects=[{string.Join(", ", (p1.S3Objects ?? new()).Select(o => o.Key + ":" + o.Size))}]");
    var p2 = await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = dBucket, Prefix = dPrefix, MaxKeys = 20 });
    Console.WriteLine($"recursive {dPrefix}: keyCount={p2.KeyCount} truncated={p2.IsTruncated} objects=[{string.Join(", ", (p2.S3Objects ?? new()).Select(o => o.Key + ":" + o.Size))}]");
    var p3 = await c.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = dBucket, Prefix = dPrefix, Delimiter = "/", MaxKeys = 20 });
    Console.WriteLine($"inside {dPrefix} delimiter: prefixes=[{string.Join(", ", p3.CommonPrefixes ?? new())}] objects=[{string.Join(", ", (p3.S3Objects ?? new()).Select(o => o.Key + ":" + o.Size))}]");
    try
    {
        var vs = await c.ListVersionsAsync(new Amazon.S3.Model.ListVersionsRequest { BucketName = dBucket, Prefix = dPrefix, MaxKeys = 20 });
        Console.WriteLine($"versions under prefix: {vs.Versions?.Count ?? 0} (delete markers {vs.Versions?.Count(x => x.IsDeleteMarker == true) ?? 0}) truncated={vs.IsTruncated}");
        foreach (var x in (vs.Versions ?? new()).Take(8)) Console.WriteLine($"   {x.Key} v={x.VersionId} latest={x.IsLatest} marker={x.IsDeleteMarker} size={x.Size}");
    }
    catch (Exception ex) { Console.WriteLine("versions: " + ex.Message); }
    try { var h = await c.GetObjectMetadataAsync(dBucket, dPrefix); Console.WriteLine($"HEAD {dPrefix}: exists size={h.ContentLength}"); }
    catch (Exception ex) { Console.WriteLine($"HEAD {dPrefix}: " + ex.Message); }
    return 0;
}

if (args.Length > 0 && args[0] == "md5")
{
    var ma = new S3Account { Name = "m", Endpoint = "127.0.0.1:9900", UseSsl = false, PathStyle = true, AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") };
    using var mc = S3ClientFactory.Create(ma);
    mc.BeforeRequestEvent += (_, e) => { if (e is Amazon.Runtime.WebServiceRequestEventArgs w && w.Request is Amazon.S3.Model.DeleteObjectsRequest) Console.WriteLine("before: headers=" + string.Join(",", w.Headers.Keys)); };
    try { var r = await mc.DeleteObjectsAsync(new Amazon.S3.Model.DeleteObjectsRequest { BucketName = "demo-webnow", Objects = new() { new Amazon.S3.Model.KeyVersion { Key = "readme.txt" } } }); Console.WriteLine("OK deleted=" + r.DeletedObjects?.Count); }
    catch (Exception ex) { Console.WriteLine("ERR " + ex.GetType().Name + ": " + ex.Message); }
    return 0;
}

if (args.Length > 0 && args[0] == "bench")
{
    // Đo xóa thư mục big-folder/ (25.000 file) qua hàng đợi tác vụ, giống bấm Xóa trong app.
    var ba = new S3Account { Name = "bench", Endpoint = "127.0.0.1:9900", UseSsl = false, PathStyle = true, AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") };
    using var bb = new S3Browser(ba);
    var bq = new TransferQueue { Parallel = 4 };
    var bt = new TransferTask { Kind = TransferKind.Delete, Browser = bb, Bucket = "demo-webnow", Key = "big-folder/", LocalPath = "", IsFolder = true };
    var btDone = new TaskCompletionSource();
    bq.Finished += t => { if (t == bt) btDone.TrySetResult(); };
    var sw0 = Stopwatch.StartNew();
    bq.Enqueue(new[] { bt });
    await btDone.Task.WaitAsync(TimeSpan.FromMinutes(20));
    using var hc = new HttpClient();
    Console.WriteLine($"BENCH {bt.State} {bt.LastError} deleted={bt.LiveDone}/{bt.LiveTotal} time={sw0.Elapsed.TotalSeconds:0.0}s {await Stats(hc)}");
    return 0;
}

static async Task<string> Stats(HttpClient hc) { try { return await hc.GetStringAsync("http://127.0.0.1:9900/__stats"); } catch (Exception ex) { return "stats: " + ex.Message; } }

int fail = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fail++; }

var acc = new S3Account { Name = "fake", Endpoint = "127.0.0.1:9900", UseSsl = false, PathStyle = true, AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") };
using var b = new S3Browser(acc);
var ct = CancellationToken.None;

var buckets = await b.ListBucketsAsync(ct);
Check(buckets.Contains("demo-webnow"), $"ListBuckets ({string.Join(",", buckets)})");

// Root của bucket: thư mục + file
var root = b.Get("demo-webnow", "");
while (!root.Complete) await b.FetchNextPageAsync(root, ct);
Check(root.Items.Any(e => e.IsFolder && e.Key == "big-folder/") && root.Items.Any(e => e.Key == "readme.txt"), $"Root listing ({root.FolderCount} folders, {root.FileCount} files)");

// Thư mục 25.000 file: trang đầu phải về sau 1 request
var sw = Stopwatch.StartNew();
var big = b.Get("demo-webnow", "big-folder/");
var first = await b.FetchNextPageAsync(big, ct);
long firstMs = sw.ElapsedMilliseconds;
Check(first.Count == 1000 && !big.Complete, $"First page 1000 items in {firstMs} ms");
int pages = 1;
while (!big.Complete) { await b.FetchNextPageAsync(big, ct); pages++; }
Check(big.Items.Count == 25000 && pages == 25, $"All 25000 items in {pages} pages, {sw.ElapsedMilliseconds} ms");
Check(big.Items.Select(i => i.Key).Distinct().Count() == 25000, "No duplicates across pages");

// Thư mục con nhiều (1500 thư mục > 1 trang)
var many = b.Get("demo-webnow", "many-folders/");
while (!many.Complete) await b.FetchNextPageAsync(many, ct);
Check(many.FolderCount == 1500 && many.FileCount == 0, $"1500 sub-folders across pages ({many.FolderCount})");

// Cache: lần 2 lấy từ cache, không gọi mạng
Check(b.TryGet("demo-webnow", "big-folder/", out var cached) && cached.Complete && cached.Items.Count == 25000, "Cache hit");

// Prefetch
sw.Restart();
await b.PrefetchAsync("demo-webnow", "photos/");
Check(b.TryGet("demo-webnow", "photos/", out var ph) && ph.Started, $"Prefetch photos/ ({sw.ElapsedMilliseconds} ms)");

// Thư mục rỗng (object "empty-folder/") không tự hiện chính nó
var empty = b.Get("demo-webnow", "empty-folder/");
await b.FetchNextPageAsync(empty, ct);
Check(empty.Items.Count == 0 && empty.Complete, "Empty folder marker hidden");

// Upload nhỏ + multipart (40 MB) qua TransferQueue
string tmp = Path.Combine(Path.GetTempPath(), "wns3-smoke");
Directory.CreateDirectory(tmp);
string small = Path.Combine(tmp, "hello.txt");
File.WriteAllText(small, "xin chào S3");
string large = Path.Combine(tmp, "large.bin");
var rnd = new byte[40 * 1024 * 1024];
new Random(1).NextBytes(rnd);
File.WriteAllBytes(large, rnd);

var q = new TransferQueue { Parallel = 4 };
var done = new TaskCompletionSource();
int finished = 0;
q.Finished += t => { if (Interlocked.Increment(ref finished) == 2) done.TrySetResult(); };
var tasks = new[]
{
    new TransferTask { Kind = TransferKind.Upload, Browser = b, Bucket = "demo-webnow", Key = "test/hello.txt", LocalPath = small },
    new TransferTask { Kind = TransferKind.Upload, Browser = b, Bucket = "demo-webnow", Key = "test/large.bin", LocalPath = large },
};
q.Enqueue(tasks);
await done.Task.WaitAsync(TimeSpan.FromMinutes(2));
Check(tasks.All(t => t.State == TransferStatus.Done), "Upload small + 40MB multipart: " + string.Join(", ", tasks.Select(t => t.State + " " + t.LastError)));

var head = await b.HeadAsync("demo-webnow", "test/large.bin", ct);
Check(head.ContentLength == rnd.Length, $"HEAD size {head.ContentLength}");

// Download lại, so sánh
string back = Path.Combine(tmp, "large-back.bin");
var d = new TransferTask { Kind = TransferKind.Download, Browser = b, Bucket = "demo-webnow", Key = "test/large.bin", LocalPath = back };
var dd = new TaskCompletionSource();
q.Finished += t => { if (t == d) dd.TrySetResult(); };
q.Enqueue(new[] { d });
await dd.Task.WaitAsync(TimeSpan.FromMinutes(2));
Check(d.State == TransferStatus.Done && File.ReadAllBytes(back).AsSpan().SequenceEqual(rnd), "Download 40MB matches");

// Stop giữa chừng tải 250MB
var big2 = new TransferTask { Kind = TransferKind.Download, Browser = b, Bucket = "demo-webnow", Key = "backups/files-2026-10-17.tar.gz", LocalPath = Path.Combine(tmp, "files.tar.gz") };
var stopTcs = new TaskCompletionSource();
q.Finished += t => { if (t == big2) stopTcs.TrySetResult(); };
q.Enqueue(new[] { big2 });
await Task.Delay(300);
q.Stop(big2);
await stopTcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
Check(big2.State == TransferStatus.Stopped && !File.Exists(big2.LocalPath + ".wns3part"), $"Stop download -> {big2.State}, no .part left");

// Tạo thư mục, xóa thư mục đệ quy
await b.CreateFolderAsync("demo-webnow", "test/sub/", ct);
var all = new List<string>();
await foreach (var o in b.ListAllAsync("demo-webnow", "test/", ct)) all.Add(o.Key);
Check(all.Count == 3, $"ListAll test/ = {all.Count} ({string.Join(",", all)})");
int failed = await b.DeleteKeysAsync("demo-webnow", all, null, ct);
var after = b.Fresh("demo-webnow", "test/");
await b.FetchNextPageAsync(after, ct);
Check(failed == 0 && after.Items.Count == 0, "Delete folder recursively");

// Presign
var url = await b.PresignAsync("demo-webnow", "readme.txt", TimeSpan.FromDays(7));
using var http = new HttpClient();
var body = await http.GetStringAsync(url);
Check(body.StartsWith("file readme.txt"), "Presigned URL works");

// Xóa 2000 key (2 lượt DeleteObjects)
var keys = Enumerable.Range(0, 2000).Select(i => $"big-folder/log-{i:D6}.txt").ToList();
failed = await b.DeleteKeysAsync("demo-webnow", keys, null, ct);
var bf = b.Fresh("demo-webnow", "big-folder/");
while (!bf.Complete) await b.FetchNextPageAsync(bf, ct);
Check(failed == 0 && bf.Items.Count == 23000, $"Batch delete 2000 -> {bf.Items.Count} left");

// Xóa thư mục qua hàng đợi tác vụ (1500 object), có tiến độ
var del = new TransferTask { Kind = TransferKind.Delete, Browser = b, Bucket = "demo-webnow", Key = "many-folders/", LocalPath = "", IsFolder = true };
var delDone = new TaskCompletionSource();
q.Finished += t => { if (t == del) delDone.TrySetResult(); };
q.Enqueue(new[] { del });
await delDone.Task.WaitAsync(TimeSpan.FromMinutes(2));
var mf = b.Fresh("demo-webnow", "many-folders/");
await b.FetchNextPageAsync(mf, ct);
Check(del.State == TransferStatus.Done && Interlocked.Read(ref del.LiveDone) == 1500 && Interlocked.Read(ref del.LiveTotal) == 1500 && mf.Items.Count == 0,
      $"Delete folder task: {del.State} {del.LiveDone}/{del.LiveTotal}, left {mf.Items.Count}");

// Dừng tác vụ xóa giữa chừng
var del2 = new TransferTask { Kind = TransferKind.Delete, Browser = b, Bucket = "demo-webnow", Key = "big-folder/", LocalPath = "", IsFolder = true };
var del2Done = new TaskCompletionSource();
q.Finished += t => { if (t == del2) del2Done.TrySetResult(); };
q.Enqueue(new[] { del2 });
await Task.Delay(400);
q.Stop(del2);
await del2Done.Task.WaitAsync(TimeSpan.FromMinutes(1));
Check(del2.State == TransferStatus.Stopped, $"Stop delete task -> {del2.State}");

// ===== Bucket có versioning (giống MinIO của người dùng) =====
{
    const string VB = "versioned-bucket";
    async Task<List<string>> RootPrefixes() { var l = b.Fresh(VB, ""); while (!l.Complete) await b.FetchNextPageAsync(l, ct); return l.Items.Select(i => i.Key).ToList(); }
    async Task<int> VersionCount(string pfx) { int n = 0; string? km = null, vm = null; while (true) { var r = await b.Client.ListVersionsAsync(new Amazon.S3.Model.ListVersionsRequest { BucketName = VB, Prefix = pfx, KeyMarker = km, VersionIdMarker = vm }); n += r.Versions?.Count ?? 0; if (r.IsTruncated != true) break; km = r.NextKeyMarker; vm = r.NextVersionIdMarker; } return n; }

    // Tái hiện lỗi: chỉ xóa object hiện hành → còn dấu xóa + bản cũ → thư mục vẫn hiện
    var cur = new List<string>();
    await foreach (var o in b.ListAllAsync(VB, "jetbackup/site-0/", ct)) cur.Add(o.Key);
    await b.DeleteKeysAsync(VB, cur, null, ct);
    var afterPlain = new List<string>();
    await foreach (var o in b.ListAllAsync(VB, "jetbackup/site-0/", ct)) afterPlain.Add(o.Key);
    Check(afterPlain.Count == 0 && (await RootPrefixes()).Contains("jetbackup/") && await VersionCount("jetbackup/site-0/") > 0,
          $"Repro MinIO: plain delete leaves ghost folder ({await VersionCount("jetbackup/site-0/")} versions/markers left)");

    // Sửa: tác vụ xóa thư mục xóa luôn mọi phiên bản
    var vt = new TransferTask { Kind = TransferKind.Delete, Browser = b, Bucket = VB, Key = "jetbackup/", LocalPath = "", IsFolder = true };
    var vtDone = new TaskCompletionSource();
    q.Finished += t => { if (t == vt) vtDone.TrySetResult(); };
    q.Enqueue(new[] { vt });
    await vtDone.Task.WaitAsync(TimeSpan.FromMinutes(2));
    var roots = await RootPrefixes();
    Check(vt.State == TransferStatus.Done && await VersionCount("jetbackup/") == 0 && !roots.Contains("jetbackup/") && roots.Contains("keep.txt"),
          $"Delete folder in versioned bucket purges versions: {vt.State} {vt.LastError}, roots=[{string.Join(",", roots)}]");

    // Xóa file lẻ: cũng xóa hết phiên bản của đúng file đó
    var ft = new TransferTask { Kind = TransferKind.Delete, Browser = b, Bucket = VB, Key = "keep.txt", LocalPath = "" };
    var ftDone = new TaskCompletionSource();
    q.Finished += t => { if (t == ft) ftDone.TrySetResult(); };
    q.Enqueue(new[] { ft });
    await ftDone.Task.WaitAsync(TimeSpan.FromMinutes(1));
    Check(ft.State == TransferStatus.Done && await VersionCount("keep.txt") == 0, "Delete file in versioned bucket purges its versions");
}

Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
return fail;
