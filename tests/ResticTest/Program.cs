// Kiểm tra trình đọc restic: vector chuẩn (RFC 8439, RFC 7914, NIST SP800-38A) + kho do restic thật tạo ra.
// Dùng: ResticTest <thư mục kho> <mật khẩu> <thư mục nguồn đã sao lưu> [s3]
//   s3: chép kho lên FakeS3 (127.0.0.1:9900, bucket demo-webnow, WebNowBackup/srv1/restic/) rồi đọc qua S3.
using System.Diagnostics;
using System.Security.Cryptography;
using WNS3Connect.Models;
using WNS3Connect.Services;
using WNS3Connect.Services.Restic;

int fails = 0;
void Ok(bool c, string what) { Console.WriteLine((c ? "  OK   " : "  FAIL ") + what); if (!c) fails++; }
byte[] H(string s) => Convert.FromHexString(s);

// ---- Vector chuẩn ----
var tag = new byte[16];
ResticCrypto.Poly1305(H("85d6be7857556d337f4452fe42d506a80103808afb0db2fd4abff6af4149f51b"), "Cryptographic Forum Research Group"u8, tag);
Ok(Convert.ToHexStringLower(tag) == "a8061dc1305136c6c22b8baf0c0127a9", "Poly1305 RFC 8439");
Ok(Convert.ToHexStringLower(ResticCrypto.Scrypt(Array.Empty<byte>(), Array.Empty<byte>(), 16, 1, 1, 64)) ==
   "77d6576238657b203b19ca42c18a0497f16b4844e3074ae8dfdffa3fede21442fcd0069ded0948f8326a753a0fc81f17e8d3e0fb2e0d3628cf35e20c38d18906", "scrypt RFC 7914 #1");
Ok(Convert.ToHexStringLower(ResticCrypto.Scrypt("password"u8.ToArray(), "NaCl"u8.ToArray(), 1024, 8, 16, 64)) ==
   "fdbabe1c9d3472007856e7190d01e9fe7c6ad7cbc8237830e77376634b3731622eaf30d92e22a3886ff109279d9830dac727afb94a83ee6d8360cbdfa2cc0640", "scrypt RFC 7914 #2");
var swK = Stopwatch.StartNew();
ResticCrypto.Scrypt("x"u8.ToArray(), new byte[64], 32768, 8, 4, 64);
Console.WriteLine($"  scrypt N=32768 r=8 p=4 (tham số restic mặc định): {swK.ElapsedMilliseconds} ms");
var ctr = new byte[32];
ResticCrypto.AesCtr(H("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4"), H("f0f1f2f3f4f5f6f7f8f9fafbfcfdfeff"), H("6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51"), ctr);
Ok(Convert.ToHexStringLower(ctr) == "601ec313775789a5b7a7f504bbf3d228f443e3ca4d62b59aca84e990cacaf5c5", "AES-256-CTR NIST F.5.5");
var k = ResticKey.FromBytes(RandomNumberGenerator.GetBytes(64));
var msg = RandomNumberGenerator.GetBytes(100_003);
Ok(ResticCrypto.Decrypt(k, ResticCrypto.Encrypt(k, msg)).AsSpan().SequenceEqual(msg), "mã hóa / giải mã khứ hồi");
Ok(ResticRepository.ParseTime("2026-10-18T02:20:26.249292812+07:00")?.ToUnixTimeSeconds() == DateTimeOffset.Parse("2026-10-17T19:20:26Z").ToUnixTimeSeconds(), "thời gian nano giây");

// ---- Mật khẩu kho trong vault WebNow (đồng bộ E2E) ----
{
    var acc = new S3Account { Name = "a", AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") };
    var plainJson = System.Text.Json.JsonSerializer.Serialize(VaultS3Account.From(acc));
    Ok(plainJson.Contains("\"resticRepos\":null"), "tài khoản chưa có kho restic: vault giữ resticRepos=null (không đổi dấu vân tay)");
    acc.ResticRepos.Add(new ResticRepoRef { Bucket = "b", Path = "WebNowBackup/srv/restic/", PasswordEnc = SecretProtector.Encrypt("pw-123") });
    var json = System.Text.Json.JsonSerializer.Serialize(VaultS3Account.From(acc));
    var back = System.Text.Json.JsonSerializer.Deserialize<VaultS3Account>(json)!.ToLocal();
    Ok(json.Contains("\"password\":\"pw-123\"") && back.ResticRepos.Count == 1 && SecretProtector.Decrypt(back.ResticRepos[0].PasswordEnc) == "pw-123"
       && back.ResticRepos[0].Path == "WebNowBackup/srv/restic/", "vault: mật khẩu kho restic đi kèm tài khoản S3 và khôi phục lại được");
    var old = System.Text.Json.JsonSerializer.Deserialize<VaultS3Account>("{\"id\":\"" + Guid.NewGuid() + "\",\"name\":\"x\"}")!.ToLocal();
    Ok(old.ResticRepos.Count == 0, "vault cũ (không có resticRepos) vẫn đọc được");
    Ok(acc.Clone().ResticRepos[0] != acc.ResticRepos[0] && acc.Clone().ResticRepos[0].Path == acc.ResticRepos[0].Path, "Clone() chép sâu danh sách kho");
}

if (args.Length >= 3 && args[0] == "corrupt")
{
    // corrupt <kho> <mật khẩu>: chép kho, làm hỏng 1 byte trong pack lớn nhất → chỉ file dùng pack đó lỗi, không còn file tạm.
    string cRepo = Path.Combine(Path.GetTempPath(), "wnr-bad-" + Guid.NewGuid().ToString("N")[..6]);
    foreach (var f in Directory.EnumerateFiles(args[1], "*", SearchOption.AllDirectories))
    {
        var dst = Path.Combine(cRepo, Path.GetRelativePath(args[1], f));
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(f, dst);
    }
    var big = new DirectoryInfo(Path.Combine(cRepo, "data")).EnumerateFiles("*", SearchOption.AllDirectories).OrderByDescending(f => f.Length).First();
    var bytes = File.ReadAllBytes(big.FullName); bytes[1000] ^= 0xFF; File.WriteAllBytes(big.FullName, bytes);
    var repo = await ResticRepository.OpenAsync(new LocalResticBackend(cRepo), args[2], default);
    var home = (await repo.LoadSnapshotsAsync(default)).First(x => x.IsHome);
    var nodes = await repo.ListDirAsync(home, home.StartPath, default);
    var plan = await repo.PlanRestoreAsync(nodes!, null, default);
    string outDir = cRepo + "-out";
    long done = 0;
    var r = await repo.RestoreAsync(plan.Items, outDir, n => Interlocked.Add(ref done, n), default);
    Console.WriteLine($"  {r.Files} file ok, {r.Failed} lỗi: {string.Join(" | ", r.Errors.Take(2))}");
    Ok(r.Failed >= 1 && r.Files >= 1, "pack hỏng → chỉ file liên quan báo lỗi, file khác vẫn khôi phục");
    Ok(done == plan.TotalBytes, "tiến độ vẫn về 100%");
    Ok(!Directory.EnumerateFiles(outDir, "*.wnrpart", SearchOption.AllDirectories).Any(), "không còn file tạm .wnrpart");
    try { Directory.Delete(cRepo, true); Directory.Delete(outDir, true); } catch { }
}
else if (args.Length >= 4 && args[0] == "full")
{
    // full <kho> <mật khẩu> <thư mục nguồn (tên cuối = thư mục đã backup)> <thư mục đích>: khôi phục snapshot mới nhất, so từng file.
    string fRepo = args[1], fPw = args[2], fSrc = Path.GetFullPath(args[3]), fOut = args[4];
    IResticBackend fb = new LocalResticBackend(fRepo);
    if (args.Length > 5 && args[5] == "s3")
    {
        var acc = new S3Account { Name = "t", Endpoint = "127.0.0.1:9900", UseSsl = false, PathStyle = true, AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") };
        var b = new S3Browser(acc);
        const string prefix = "WebNowBackup/srv2/restic/";
        var files = Directory.EnumerateFiles(fRepo, "*", SearchOption.AllDirectories).ToList();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (f, c) =>
            await b.Client.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = "demo-webnow", Key = prefix + Path.GetRelativePath(fRepo, f).Replace(Path.DirectorySeparatorChar, '/'), FilePath = f }, c));
        Console.WriteLine($"  đã chép {files.Count} file kho lên FakeS3 (mỗi request trễ ~150 ms)");
        fb = new S3ResticBackend(b.Client, "demo-webnow", prefix);
    }
    var swOpen = Stopwatch.StartNew();
    var repo = await ResticRepository.OpenAsync(fb, fPw, default);
    Console.WriteLine($"  mở kho {swOpen.ElapsedMilliseconds} ms");
    var snaps = await repo.LoadSnapshotsAsync(default);
    Ok(snaps.Count >= 2, $"kho v{repo.Version}: {snaps.Count} snapshot");
    string want = Path.GetFileName(fSrc);
    foreach (var (snap, idx) in snaps.Select((s, i) => (s, i)))
    {
        // Gốc snapshot trên Windows là /C/Users/... → đi xuống tới thư mục cùng tên với nguồn.
        string path = "/";
        for (int depth = 0; depth < 40; depth++)
        {
            var ls = await repo.ListDirAsync(snap, path, default);
            if (ls!.Any(n => n.Name == want && n.IsDir)) { path = path.TrimEnd('/') + "/" + want; break; }
            path = path.TrimEnd('/') + "/" + ls!.Single(n => n.IsDir).Name;
        }
        var nodes = await repo.ListDirAsync(snap, path, default);
        string target = Path.Combine(fOut, "s" + idx);
        var sw = Stopwatch.StartNew();
        var plan = await repo.PlanRestoreAsync(nodes!, null, default);
        long planMs = sw.ElapsedMilliseconds;
        long done = 0;
        var r = await repo.RestoreAsync(plan.Items, target, n => Interlocked.Add(ref done, n), default);
        Console.WriteLine($"  snapshot {snap.ShortId}: {r.Files} file, {r.Dirs} thư mục, {S3Entry.FormatSize(r.Bytes)}: lập kế hoạch {planMs} ms, ghi {sw.ElapsedMilliseconds - planMs} ms, lỗi {r.Failed}");
        foreach (var e in r.Errors.Take(5)) Console.WriteLine("    " + e);
        Ok(r.Failed == 0 && done == plan.TotalBytes, "tiến độ khớp tổng");
        int diff = 0, same = 0;
        foreach (var f in Directory.EnumerateFiles(fSrc, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(fSrc, f);
            string o = ResticRepository.LocalPath(target, rel.Replace(Path.DirectorySeparatorChar, '/'));
            if (rel.EndsWith("dup1.bin") && idx == 1) { same++; continue; } // bản cũ chưa có file này
            if (!File.Exists(o)) { diff++; if (diff < 4) Console.WriteLine("    thiếu " + rel); continue; }
            bool eq = SHA256.HashData(File.ReadAllBytes(f)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(o)));
            if (rel.EndsWith("dup1.bin") && idx == 1) { same++; continue; } // bản cũ chưa có file này
            if (eq) same++; else { diff++; if (diff < 4) Console.WriteLine("    khác " + rel); }
        }
        Ok(diff == 0, $"{same} file khớp nguồn");
    }
}
else if (args.Length >= 3)
{
    string repoDir = args[0], pw = args[1], srcDir = args[2];
    IResticBackend backend = new LocalResticBackend(repoDir);
    if (args.Length > 3 && args[3] == "s3")
    {
        var acc = new S3Account { Name = "t", Endpoint = "127.0.0.1:9900", UseSsl = false, PathStyle = true, AccessKey = "AK", SecretKeyEnc = SecretProtector.Encrypt("SK") };
        var b = new S3Browser(acc);
        const string prefix = "WebNowBackup/srv1/restic/";
        int n = 0;
        foreach (var f in Directory.EnumerateFiles(repoDir, "*", SearchOption.AllDirectories))
        {
            string key = prefix + Path.GetRelativePath(repoDir, f).Replace(Path.DirectorySeparatorChar, '/');
            await b.Client.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest { BucketName = "demo-webnow", Key = key, FilePath = f });
            n++;
        }
        Console.WriteLine($"  đã chép {n} file kho lên FakeS3");
        backend = new S3ResticBackend(b.Client, "demo-webnow", prefix);
    }

    try { await ResticRepository.OpenAsync(backend, "sai-mat-khau", default); Ok(false, "sai mật khẩu bị từ chối"); }
    catch (ResticWrongPasswordException) { Ok(true, "sai mật khẩu bị từ chối"); }

    var sw = Stopwatch.StartNew();
    var repo = await ResticRepository.OpenAsync(backend, pw, default);
    Ok(repo.Version == 2, $"mở kho v{repo.Version} id {repo.RepoId[..8]} ({sw.ElapsedMilliseconds} ms)");
    var snaps = await repo.LoadSnapshotsAsync(default);
    Ok(snaps.Count >= 2, $"{snaps.Count} snapshot");
    var home = snaps.First(s => s.IsHome);
    var db = snaps.First(s => s.IsDatabase);
    Ok(home.IsWnb && home.WnbUser == "u1" && home.StartPath == "home/u1", $"snapshot home: {home.KindText} · {home.UserText} · start {home.StartPath}");
    Ok(db.Databases.SequenceEqual(new[] { "u1_wp" }), "snapshot db: " + db.KindText);
    await repo.EnsureIndexAsync(default);
    Ok(repo.BlobCount > 0, $"index: {repo.BlobCount} blob / {repo.PackCount} pack");

    var list = await repo.ListDirAsync(home, home.StartPath, default);
    Ok(list != null && list.Any(n => n.Name == "public_html" && n.IsDir) && list.Any(n => n.Name == "Tài liệu"), "duyệt home/u1: " + string.Join(", ", list?.Select(n => n.Name) ?? []));
    Ok(await repo.ListDirAsync(home, "home/khong-co", default) == null, "thư mục không có → null");

    string outDir = Path.Combine(Path.GetTempPath(), "wnr-test-" + Guid.NewGuid().ToString("N")[..6]);
    sw.Restart();
    var plan = await repo.PlanRestoreAsync(list!, null, default);
    long done = 0;
    var r = await repo.RestoreAsync(plan.Items, outDir, n => Interlocked.Add(ref done, n), default);
    Console.WriteLine($"  khôi phục {r.Files} file, {r.Dirs} thư mục, {S3Entry.FormatSize(r.Bytes)} trong {sw.ElapsedMilliseconds} ms; lỗi {r.Failed}, bỏ qua {r.Skipped}");
    Ok(r.Failed == 0 && done == plan.TotalBytes && r.Bytes == plan.TotalBytes, $"tiến độ = tổng ({S3Entry.FormatSize(plan.TotalBytes)})");
    int same = 0, diff = 0;
    string srcHome = Path.Combine(srcDir, "home", "u1");
    foreach (var f in Directory.EnumerateFiles(srcHome, "*", SearchOption.AllDirectories))
    {
        string rel = Path.GetRelativePath(srcHome, f);
        string o = Path.Combine(outDir, rel);
        bool eq = File.Exists(o) && SHA256.HashData(File.ReadAllBytes(f)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(o)));
        bool mt = File.Exists(o) && Math.Abs((File.GetLastWriteTimeUtc(f) - File.GetLastWriteTimeUtc(o)).TotalSeconds) < 1;
        if (eq && mt) same++; else { diff++; Console.WriteLine("    khác: " + rel + (eq ? " (giờ sửa)" : "")); }
    }
    Ok(diff == 0 && same >= 5, $"{same} file giống hệt nguồn (nội dung + giờ sửa)");

    var dbNodes = await repo.ListDirAsync(db, db.StartPath, default);
    var sql = dbNodes!.First(n => n.IsFile);
    using var ms = new MemoryStream();
    await repo.WriteFileAsync(sql, ms, null, default);
    Ok(System.Text.Encoding.UTF8.GetString(ms.ToArray()).StartsWith("CREATE TABLE x"), $"dump database {sql.Name} ({ms.Length} B)");
    try { Directory.Delete(outDir, true); } catch { }
}

Console.WriteLine(fails == 0 ? "TẤT CẢ OK" : $"{fails} LỖI");
return fails == 0 ? 0 : 1;
