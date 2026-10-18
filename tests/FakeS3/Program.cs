// Fake S3 server (path-style, không kiểm tra chữ ký) để test WNS3Connect mà không đụng S3 thật.
// Chạy: dotnet run --project tests/FakeS3 -- --urls http://127.0.0.1:9900
// Biến môi trường: FAKES3_LATENCY (ms, mặc định 150) — độ trễ giả lập mạng cho mỗi request.
//                  FAKES3_REQUIRE_MD5=1 — giống Ceph/CloudFly: DeleteObjects thiếu Content-MD5 bị trả 400.
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();

int latency = int.TryParse(Environment.GetEnvironmentVariable("FAKES3_LATENCY"), out var l) ? l : 150;
bool requireMd5 = Environment.GetEnvironmentVariable("FAKES3_REQUIRE_MD5") == "1";
XNamespace ns = "http://s3.amazonaws.com/doc/2006-03-01/";
var buckets = new ConcurrentDictionary<string, Bucket>();
var uploads = new ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>>();
var apiUsers = new ConcurrentDictionary<string, FakeUser>();
var apiTokens = new ConcurrentDictionary<string, FakeUser>();
long requestCount = 0, batchDeletes = 0, singleDeletes = 0;

Seed();



app.Run(async ctx =>
{
    Interlocked.Increment(ref requestCount);
    if (ctx.Request.Path == "/__stats") { await ctx.Response.WriteAsync($"requests={Interlocked.Read(ref requestCount)} deleteObjects={Interlocked.Read(ref batchDeletes)} deleteObject={Interlocked.Read(ref singleDeletes)}"); return; }
    var req = ctx.Request;
    if (req.Path.StartsWithSegments("/api/v1", out var apiPath)) { await Api(ctx, apiPath.Value ?? ""); return; }
    if (latency > 0) await Task.Delay(latency);
    var q = req.Query;
    string path = Uri.UnescapeDataString(req.Path.Value ?? "/").TrimStart('/');
    int slash = path.IndexOf('/');
    string bucketName = slash < 0 ? path : path[..slash];
    string key = slash < 0 ? "" : path[(slash + 1)..];
    string method = req.Method;

    try
    {
        if (bucketName.Length == 0)
        {
            if (method != "GET") { await Error(ctx, 405, "MethodNotAllowed", ""); return; }
            var x = new XElement(ns + "ListAllMyBucketsResult",
                new XElement(ns + "Owner", new XElement(ns + "ID", "fake"), new XElement(ns + "DisplayName", "fake")),
                new XElement(ns + "Buckets", buckets.Values.OrderBy(b => b.Name, StringComparer.Ordinal).Select(b =>
                    new XElement(ns + "Bucket", new XElement(ns + "Name", b.Name), new XElement(ns + "CreationDate", Iso(b.Created))))));
            await Xml(ctx, x);
            return;
        }

        if (key.Length == 0)
        {
            switch (method)
            {
                case "PUT":
                    if (!buckets.TryAdd(bucketName, new Bucket(bucketName))) { await Error(ctx, 409, "BucketAlreadyOwnedByYou", "exists"); return; }
                    ctx.Response.StatusCode = 200; return;
                case "HEAD":
                    ctx.Response.StatusCode = buckets.ContainsKey(bucketName) ? 200 : 404; return;
            }
            if (!buckets.TryGetValue(bucketName, out var b)) { await Error(ctx, 404, "NoSuchBucket", "no bucket"); return; }
            switch (method)
            {
                case "DELETE":
                    if (!b.Objects.IsEmpty) { await Error(ctx, 409, "BucketNotEmpty", "not empty"); return; }
                    buckets.TryRemove(bucketName, out _); ctx.Response.StatusCode = 204; return;
                case "GET" when q.ContainsKey("versions"):
                    await ListVersions(ctx, b); return;
                case "GET" when q.ContainsKey("location"):
                    await Xml(ctx, new XElement(ns + "LocationConstraint", "")); return;
                case "GET":
                    await List(ctx, b); return;
                case "POST" when q.ContainsKey("delete"):
                {
                    if (requireMd5 && !req.Headers.ContainsKey("Content-MD5")) { await Error(ctx, 400, "InvalidRequest", "Missing required header for this request: Content-Md5"); return; }
                    Interlocked.Increment(ref batchDeletes);
                    using var bms = new MemoryStream();
                    await req.Body.CopyToAsync(bms);
                    var raw = bms.ToArray();
                    if (req.Headers.TryGetValue("Content-MD5", out var md5h) && md5h.ToString() != Convert.ToBase64String(MD5.HashData(raw))) { await Error(ctx, 400, "BadDigest", "Content-MD5 mismatch"); return; }
                    var body = XDocument.Parse(Encoding.UTF8.GetString(raw));
                    var items = body.Descendants().Where(e => e.Name.LocalName == "Object")
                        .Select(o => (key: o.Elements().First(x => x.Name.LocalName == "Key").Value, ver: o.Elements().FirstOrDefault(x => x.Name.LocalName == "VersionId")?.Value)).ToList();
                    foreach (var (k, v) in items) b.Delete(k, v);
                    await Xml(ctx, new XElement(ns + "DeleteResult", items.Select(i => new XElement(ns + "Deleted", new XElement(ns + "Key", i.key)))));
                    return;
                }
            }
            await Error(ctx, 405, "MethodNotAllowed", method);
            return;
        }

        if (!buckets.TryGetValue(bucketName, out var bk)) { await Error(ctx, 404, "NoSuchBucket", "no bucket"); return; }

        if (method == "POST" && q.ContainsKey("uploads"))
        {
            string id = Guid.NewGuid().ToString("N");
            uploads[id] = new();
            await Xml(ctx, new XElement(ns + "InitiateMultipartUploadResult", new XElement(ns + "Bucket", bucketName), new XElement(ns + "Key", key), new XElement(ns + "UploadId", id)));
            return;
        }
        if (method == "PUT" && q.TryGetValue("uploadId", out var upId))
        {
            int part = int.Parse(q["partNumber"]!);
            var data = await ReadPayload(req);
            uploads[upId!][part] = data;
            ctx.Response.Headers.ETag = "\"" + Md5(data) + "\"";
            return;
        }
        if (method == "POST" && q.TryGetValue("uploadId", out var cId))
        {
            await ReadBody(req);
            var parts = uploads[cId!];
            using var ms = new MemoryStream();
            foreach (var p in parts.OrderBy(p => p.Key)) ms.Write(p.Value);
            uploads.TryRemove(cId!, out _);
            var o = new Obj { Data = ms.ToArray(), ContentType = req.ContentType ?? "application/octet-stream" };
            o.Size = o.Data.Length; o.ETag = Md5(o.Data) + "-" + parts.Count;
            bk.Put(key, o);
            await Xml(ctx, new XElement(ns + "CompleteMultipartUploadResult", new XElement(ns + "Bucket", bucketName), new XElement(ns + "Key", key), new XElement(ns + "ETag", "\"" + o.ETag + "\"")));
            return;
        }
        if (method == "DELETE" && q.TryGetValue("uploadId", out var aId)) { uploads.TryRemove(aId!, out _); ctx.Response.StatusCode = 204; return; }

        switch (method)
        {
            case "PUT":
            {
                var data = await ReadPayload(req);
                var o = new Obj { Data = data, Size = data.Length, ETag = Md5(data), ContentType = req.ContentType ?? "application/octet-stream" };
                bk.Put(key, o);
                ctx.Response.Headers.ETag = "\"" + o.ETag + "\"";
                return;
            }
            case "DELETE":
                Interlocked.Increment(ref singleDeletes); bk.Delete(key, q.TryGetValue("versionId", out var dv) ? dv.ToString() : null); ctx.Response.StatusCode = 204; return;
            case "HEAD":
            case "GET":
            {
                if (!bk.Objects.TryGetValue(key, out var o)) { await Error(ctx, 404, "NoSuchKey", "no key"); return; }
                var res = ctx.Response;
                res.Headers.ETag = "\"" + o.ETag + "\"";
                res.Headers.LastModified = o.Modified.ToString("R");
                res.Headers["x-amz-meta-seeded"] = o.Data == null ? "true" : "false";
                res.ContentType = o.ContentType;
                long start = 0, end = o.Size - 1;
                if (req.Headers.Range.Count > 0 && req.Headers.Range[0] is { } range && range.StartsWith("bytes="))
                {
                    var se = range[6..].Split('-');
                    start = long.Parse(se[0]);
                    if (se[1].Length > 0) end = Math.Min(end, long.Parse(se[1]));
                    res.StatusCode = 206;
                    res.Headers.ContentRange = $"bytes {start}-{end}/{o.Size}";
                }
                res.ContentLength = Math.Max(0, end - start + 1);
                if (method == "HEAD") return;
                if (o.Data != null) { await res.Body.WriteAsync(o.Data.AsMemory((int)start, (int)(end - start + 1))); return; }
                // Object seed lớn: sinh dữ liệu theo khối, ghi chậm vừa phải để thấy tiến độ.
                var buf = new byte[64 * 1024];
                for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(i * 31);
                long left = end - start + 1;
                while (left > 0)
                {
                    int n = (int)Math.Min(buf.Length, left);
                    await res.Body.WriteAsync(buf.AsMemory(0, n), ctx.RequestAborted);
                    left -= n;
                }
                return;
            }
        }
        await Error(ctx, 405, "MethodNotAllowed", method);
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        if (!ctx.Response.HasStarted) await Error(ctx, 500, "InternalError", ex.Message);
    }
});

app.Run();

// ===== API tài khoản giả (giống server WNTerm: /api/v1/...) — chỉ để test tương thích mã hóa/vault =====
async Task Api(HttpContext ctx, string p)
{
    var req = ctx.Request;
    System.Text.Json.Nodes.JsonObject B() => (System.Text.Json.Nodes.JsonNode.Parse(new StreamReader(req.Body).ReadToEndAsync().Result) as System.Text.Json.Nodes.JsonObject) ?? new();
    async Task J(int code, object o) { ctx.Response.StatusCode = code; await ctx.Response.WriteAsJsonAsync(o); }
    FakeUser? Auth()
    {
        string h = req.Headers.Authorization.ToString();
        return h.StartsWith("Bearer ") && apiTokens.TryGetValue(h[7..], out var u) ? u : null;
    }
    string NewToken(FakeUser u) { var t = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24)); apiTokens[t] = u; return t; }
    switch (req.Method + " " + p)
    {
        case "POST /register":
        {
            var b = B(); string email = b["email"]!.GetValue<string>();
            if (apiUsers.ContainsKey(email)) { await J(409, new { ok = false, error = "email_taken", message = "taken" }); return; }
            var u = new FakeUser { Email = email, AuthKey = b["authKey"]!.GetValue<string>(), WrappedPw = b["wrappedPw"]!.GetValue<string>(), Kdf = b["kdfIterations"]!.GetValue<int>() };
            apiUsers[email] = u;
            await J(201, new { ok = true, token = NewToken(u), email });
            return;
        }
        case "POST /prelogin":
        {
            var b = B(); apiUsers.TryGetValue(b["email"]!.GetValue<string>(), out var u);
            await J(200, new { ok = true, kdfIterations = u?.Kdf ?? 600000 });
            return;
        }
        case "POST /login":
        {
            var b = B();
            if (!apiUsers.TryGetValue(b["email"]!.GetValue<string>(), out var u) || u.AuthKey != b["authKey"]!.GetValue<string>())
            { await J(401, new { ok = false, error = "bad_credentials", message = "Email hoặc mật khẩu không đúng." }); return; }
            await J(200, new { ok = true, token = NewToken(u), email = u.Email, wrappedPw = u.WrappedPw, kdfIterations = u.Kdf, vault = new { version = u.Blobs.Count } });
            return;
        }
    }
    var me = Auth();
    if (me == null) { await J(401, new { ok = false, error = "unauthorized", message = "Chưa đăng nhập." }); return; }
    switch (req.Method + " " + p)
    {
        case "GET /me": await J(200, new { ok = true, email = me.Email }); return;
        case "POST /logout": apiTokens.TryRemove(req.Headers.Authorization.ToString()[7..], out _); await J(200, new { ok = true }); return;
        case "GET /vault":
        {
            int cur = me.Blobs.Count;
            int ver = int.TryParse(req.Query["version"], out var v) ? v : cur;
            await J(200, new { ok = true, version = ver, current = cur, blob = ver == 0 ? null : Convert.ToBase64String(me.Blobs[ver - 1]) });
            return;
        }
        case "PUT /vault":
        {
            var b = B(); int bas = b["baseVersion"]!.GetValue<int>();
            lock (me)
            {
                if (bas != me.Blobs.Count) { J(409, new { ok = false, error = "conflict", message = "conflict", serverVersion = me.Blobs.Count }).Wait(); return; }
                me.Blobs.Add(Convert.FromBase64String(b["blob"]!.GetValue<string>()));
            }
            await J(200, new { ok = true, version = me.Blobs.Count });
            return;
        }
    }
    await J(404, new { ok = false, error = "not_found", message = p });
}

async Task ListVersions(HttpContext ctx, Bucket b)
{
    var q = ctx.Request.Query;
    string prefix = q["prefix"].ToString();
    int max = int.TryParse(q["max-keys"], out var m) ? Math.Clamp(m, 1, 1000) : 1000;
    string km = q["key-marker"].ToString(), vm = q["version-id-marker"].ToString();
    var flat = new List<(string key, Ver v, bool latest)>();
    foreach (var kv in b.Versions.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(x => x.Key, StringComparer.Ordinal))
    {
        List<Ver> list; lock (kv.Value) list = kv.Value.ToList();
        for (int i = list.Count - 1; i >= 0; i--) flat.Add((kv.Key, list[i], i == list.Count - 1));
    }
    int start = 0;
    if (km.Length > 0)
    {
        int idx = flat.FindIndex(f => f.key == km && f.v.Id == vm);
        start = idx >= 0 ? idx + 1 : flat.FindIndex(f => string.CompareOrdinal(f.key, km) > 0);
        if (start < 0) start = flat.Count;
    }
    var page = flat.Skip(start).Take(max).ToList();
    bool truncated = start + page.Count < flat.Count;
    var x = new XElement(ns + "ListVersionsResult",
        new XElement(ns + "Name", b.Name), new XElement(ns + "Prefix", prefix), new XElement(ns + "MaxKeys", max),
        new XElement(ns + "IsTruncated", truncated ? "true" : "false"),
        truncated ? new XElement(ns + "NextKeyMarker", page[^1].key) : null,
        truncated ? new XElement(ns + "NextVersionIdMarker", page[^1].v.Id) : null,
        page.Select(p => p.v.Marker
            ? new XElement(ns + "DeleteMarker", new XElement(ns + "Key", p.key), new XElement(ns + "VersionId", p.v.Id), new XElement(ns + "IsLatest", p.latest ? "true" : "false"), new XElement(ns + "LastModified", Iso(p.v.Modified)))
            : new XElement(ns + "Version", new XElement(ns + "Key", p.key), new XElement(ns + "VersionId", p.v.Id), new XElement(ns + "IsLatest", p.latest ? "true" : "false"),
                new XElement(ns + "LastModified", Iso(p.v.Modified)), new XElement(ns + "ETag", "\"v\""), new XElement(ns + "Size", p.v.Size), new XElement(ns + "StorageClass", "STANDARD"))));
    await Xml(ctx, x);
}

async Task List(HttpContext ctx, Bucket b)
{
    var q = ctx.Request.Query;
    string prefix = q["prefix"].ToString();
    string delimiter = q["delimiter"].ToString();
    int max = int.TryParse(q["max-keys"], out var m) ? Math.Clamp(m, 1, 1000) : 1000;
    string token = q["continuation-token"].ToString();
    string after = token.Length > 0 ? Encoding.UTF8.GetString(Convert.FromBase64String(token)) : q["start-after"].ToString();

    var all = b.Versioned && delimiter.Length > 0 ? b.Objects.Keys.Concat(b.Versions.Where(v => v.Value.Count > 0).Select(v => v.Key)).Distinct() : b.Objects.Keys;
    var keys = all.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal);
    var contents = new List<(string key, Obj o)>();
    var prefixes = new List<string>();
    string? last = null;
    bool truncated = false;
    foreach (var k in keys)
    {
        if (!b.Objects.ContainsKey(k) && delimiter.Length > 0 && k.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal) < 0) continue;
        string entry = k;
        bool isPrefix = false;
        if (delimiter.Length > 0)
        {
            int i = k.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
            if (i >= 0) { entry = k[..(i + delimiter.Length)]; isPrefix = true; }
        }
        if (after.Length > 0 && string.CompareOrdinal(entry, after) <= 0) continue;
        if (isPrefix && prefixes.Count > 0 && prefixes[^1] == entry) continue;
        if (contents.Count + prefixes.Count >= max) { truncated = true; break; }
        if (isPrefix) prefixes.Add(entry); else if (b.Objects.TryGetValue(k, out var co)) contents.Add((k, co)); else continue;
        last = entry;
    }

    var x = new XElement(ns + "ListBucketResult",
        new XElement(ns + "Name", b.Name),
        new XElement(ns + "Prefix", prefix),
        new XElement(ns + "KeyCount", contents.Count + prefixes.Count),
        new XElement(ns + "MaxKeys", max),
        delimiter.Length > 0 ? new XElement(ns + "Delimiter", delimiter) : null,
        new XElement(ns + "IsTruncated", truncated ? "true" : "false"),
        token.Length > 0 ? new XElement(ns + "ContinuationToken", token) : null,
        truncated && last != null ? new XElement(ns + "NextContinuationToken", Convert.ToBase64String(Encoding.UTF8.GetBytes(last))) : null,
        contents.Select(c => new XElement(ns + "Contents",
            new XElement(ns + "Key", c.key),
            new XElement(ns + "LastModified", Iso(c.o.Modified)),
            new XElement(ns + "ETag", "\"" + c.o.ETag + "\""),
            new XElement(ns + "Size", c.o.Size),
            new XElement(ns + "StorageClass", "STANDARD"))),
        prefixes.Select(p => new XElement(ns + "CommonPrefixes", new XElement(ns + "Prefix", p))));
    await Xml(ctx, x);
}

static string Iso(DateTime d) => d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

static string Md5(byte[] d) => Convert.ToHexStringLower(MD5.HashData(d));

static async Task Xml(HttpContext ctx, XElement x)
{
    ctx.Response.ContentType = "application/xml";
    await ctx.Response.WriteAsync("<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + x.ToString(SaveOptions.DisableFormatting));
}

static async Task Error(HttpContext ctx, int status, string code, string msg)
{
    ctx.Response.StatusCode = status;
    if (ctx.Request.Method == "HEAD") return;
    ctx.Response.ContentType = "application/xml";
    await ctx.Response.WriteAsync($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>{System.Security.SecurityElement.Escape(msg)}</Message></Error>");
}

static async Task<string> ReadBody(HttpRequest req)
{
    using var sr = new StreamReader(req.Body);
    return await sr.ReadToEndAsync();
}

/// <summary>Đọc body, giải mã aws-chunked (STREAMING-...) nếu SDK gửi kiểu đó.</summary>
static async Task<byte[]> ReadPayload(HttpRequest req)
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var raw = ms.ToArray();
    string sha = req.Headers["x-amz-content-sha256"].ToString();
    bool chunked = sha.StartsWith("STREAMING-", StringComparison.Ordinal) || req.Headers.ContentEncoding.ToString().Contains("aws-chunked");
    if (!chunked) return raw;
    var outp = new MemoryStream();
    int pos = 0;
    while (pos < raw.Length)
    {
        int eol = IndexOfCrlf(raw, pos);
        if (eol < 0) break;
        string header = Encoding.ASCII.GetString(raw, pos, eol - pos);
        int semi = header.IndexOf(';');
        int size = Convert.ToInt32(semi >= 0 ? header[..semi] : header, 16);
        pos = eol + 2;
        if (size == 0) break;
        outp.Write(raw, pos, size);
        pos += size + 2;
    }
    return outp.ToArray();
}

static int IndexOfCrlf(byte[] b, int from)
{
    for (int i = from; i < b.Length - 1; i++) if (b[i] == '\r' && b[i + 1] == '\n') return i;
    return -1;
}

void Seed()
{
    var demo = new Bucket("demo-webnow");
    var now = DateTime.UtcNow;
    void Add(Bucket b, string k, long size, int ageMin = 0)
    {
        var data = size <= 4096 ? Encoding.UTF8.GetBytes(("file " + k + "\n").PadRight((int)Math.Max(1, size), '.')) : null;
        b.Objects[k] = new Obj { Data = data, Size = data?.Length ?? size, ETag = Md5(Encoding.UTF8.GetBytes(k)), Modified = now.AddMinutes(-ageMin), ContentType = "application/octet-stream" };
    }
    for (int i = 0; i < 25000; i++) Add(demo, $"big-folder/log-{i:D6}.txt", 100 + i % 3000, i);
    for (int i = 0; i < 1500; i++) Add(demo, $"many-folders/site-{i:D4}/index.html", 500, i);
    for (int i = 0; i < 40; i++) Add(demo, $"photos/2026/{i % 12 + 1:D2}/IMG_{i:D4}.jpg", 2_000_000 + i * 1000, i);
    Add(demo, "backups/db-2026-10-17.sql.gz", 60L * 1024 * 1024, 5);
    Add(demo, "backups/files-2026-10-17.tar.gz", 250L * 1024 * 1024, 5);
    Add(demo, "readme.txt", 120);
    Add(demo, "empty-folder/", 0);
    buckets[demo.Name] = demo;

    var vb = new Bucket("versioned-bucket") { Versioned = true };
    for (int i = 0; i < 1200; i++)
    {
        string k = $"jetbackup/site-{i % 3}/snap.{i}/file.bin";
        vb.Put(k, new Obj { Data = Encoding.UTF8.GetBytes("v1"), Size = 2, ETag = "a" });
        vb.Put(k, new Obj { Data = Encoding.UTF8.GetBytes("v2"), Size = 2, ETag = "b" });
    }
    vb.Put("keep.txt", new Obj { Data = Encoding.UTF8.GetBytes("keep"), Size = 4, ETag = "c" });
    buckets[vb.Name] = vb;

    var b2 = new Bucket("control2-webnow-vn-backup");
    Add(b2, "control2-webnow-vn-backup/admin.tar.zst", 5L * 1024 * 1024, 60);
    buckets[b2.Name] = b2;
}

class Bucket(string name)
{
    public string Name { get; } = name;
    public DateTime Created { get; } = DateTime.UtcNow.AddDays(-30);
    public ConcurrentDictionary<string, Obj> Objects { get; } = new(StringComparer.Ordinal);
    /// <summary>Bucket có versioning (mô phỏng MinIO): xóa thường chỉ thêm dấu xóa, bản cũ còn nguyên.</summary>
    public bool Versioned { get; init; }
    public ConcurrentDictionary<string, List<Ver>> Versions { get; } = new(StringComparer.Ordinal);

    public void Put(string key, Obj o)
    {
        Objects[key] = o;
        if (!Versioned) return;
        var list = Versions.GetOrAdd(key, _ => new());
        lock (list) list.Add(new Ver { Id = Guid.NewGuid().ToString(), Size = o.Size, Obj = o });
    }

    public void Delete(string key, string? versionId)
    {
        if (!Versioned) { Objects.TryRemove(key, out _); return; }
        var list = Versions.GetOrAdd(key, _ => new());
        lock (list)
        {
            if (string.IsNullOrEmpty(versionId))
            {
                Objects.TryRemove(key, out _);
                list.Add(new Ver { Id = Guid.NewGuid().ToString(), Marker = true });
                return;
            }
            list.RemoveAll(v => v.Id == versionId);
            if (list.Count == 0) { Versions.TryRemove(key, out _); Objects.TryRemove(key, out _); return; }
            var latest = list[^1];
            if (latest.Marker || latest.Obj == null) Objects.TryRemove(key, out _); else Objects[key] = latest.Obj;
        }
    }
}

class Obj
{
    public byte[]? Data;
    public long Size;
    public string ETag = "";
    public string ContentType = "application/octet-stream";
    public DateTime Modified = DateTime.UtcNow;
}

class FakeUser
{
    public string Email = "", AuthKey = "", WrappedPw = "";
    public int Kdf;
    public List<byte[]> Blobs = new();
}

class Ver
{
    public string Id = "";
    public bool Marker;
    public long Size;
    public Obj? Obj;
    public DateTime Modified = DateTime.UtcNow;
}
