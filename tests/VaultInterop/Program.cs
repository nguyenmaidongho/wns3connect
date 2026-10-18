// Test tương thích WNS3Connect ↔ WNTerm trên server PHP THẬT (snterm/server) chạy local:
//   WNTERM_CONFIG=<test-config.php> php -S 127.0.0.1:8099 -t public tools/router.php
// Tham số: API base (mặc định http://127.0.0.1:8099/api/v1).
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WN = WNTerm.Services;
using WNM = WNTerm.Models;
using S3 = WNS3Connect.Services;
using WNS3Connect.Models;

string Api = args.Length > 0 ? args[0] : "http://127.0.0.1:8099/api/v1";
int fail = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) fail++; }

string root = Path.Combine(Path.GetTempPath(), "wns3-interop-" + Guid.NewGuid().ToString("N")[..6]);
string email = $"interop-{Guid.NewGuid():N}@test.local", pw = "Mật khẩu thử 123!";
using var http = new HttpClient();

// ----- Đăng ký (giống trang web /dang-ky: có tos) -----
static (byte[] auth, byte[] enc) Derive(string password, string email)
{
    byte[] m = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password.Normalize(NormalizationForm.FormC)),
        Encoding.UTF8.GetBytes("wnterm:v1:" + email.Trim().ToLowerInvariant()), 600000, HashAlgorithmName.SHA256, 32);
    return (HKDF.DeriveKey(HashAlgorithmName.SHA256, m, 32, [], Encoding.UTF8.GetBytes("wnterm-auth")),
            HKDF.DeriveKey(HashAlgorithmName.SHA256, m, 32, [], Encoding.UTF8.GetBytes("wnterm-enc")));
}
var (authKey, encKey) = Derive(pw, email);
byte[] vaultKey = RandomNumberGenerator.GetBytes(32);
var reg = await http.PostAsJsonAsync(Api + "/register", new
{
    email, authKey = Convert.ToBase64String(authKey), kdfIterations = 600000,
    wrappedPw = Convert.ToBase64String(S3.WebNowAccount.Seal(encKey, vaultKey)),
    recoveryAuth = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    wrappedRecovery = Convert.ToBase64String(S3.WebNowAccount.Seal(RandomNumberGenerator.GetBytes(32), vaultKey)),
    deviceName = "interop", tos = "2026-10-09"
});
string regToken = (await reg.Content.ReadFromJsonAsync<JsonObject>())?["token"]?.GetValue<string>() ?? "";
Check(reg.IsSuccessStatusCode && regToken.Length > 0, $"Register on real PHP server ({(int)reg.StatusCode})");

// ----- WNTerm (code thật): đăng nhập, 2 VM -----
var wnPaths = new WN.AppPaths(Path.Combine(root, "wnterm"), Path.Combine(root, "wnterm-local"));
Environment.SetEnvironmentVariable("WNTERM_DATA_DIR", wnPaths.DataDir);
var wnSettingsStore = new WN.SettingsStore(wnPaths);
var wnSettings = wnSettingsStore.Load();
wnSettings.AccountApiUrl = Api;
wnSettingsStore.Save(wnSettings);
var wnSessions = new WN.SessionStore(wnPaths);
wnSessions.Save(new[]
{
    new WNM.SessionInfo { Name = "web1", Host = "10.0.0.1", Username = "root", EncryptedPassword = WN.SecretProtector.Encrypt("pw1") },
    new WNM.SessionInfo { Name = "web2", Host = "10.0.0.2", Username = "admin" },
});
var wn = new WN.AccountService(wnSettings, wnSettingsStore, wnSessions, wnPaths);
await wn.LoginAsync(email, pw);
var r1 = await wn.SyncAsync();
Check(r1.ServerVersion == 1 && r1.Total == 2, $"WNTerm login + sync -> vault v{r1.ServerVersion}, {r1.Total} VM");

// ----- Dữ liệu cũ: WNS3Connect 1.0/1.1 đã ghi s3Accounts vào vault chung -----
async Task<JsonObject> GetJson(string url, string token)
{
    using var req = new HttpRequestMessage(HttpMethod.Get, url);
    req.Headers.Authorization = new("Bearer", token);
    using var res = await http.SendAsync(req);
    return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
}
async Task<HttpResponseMessage> PutJson(string url, string token, object body)
{
    using var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = JsonContent.Create(body) };
    req.Headers.Authorization = new("Bearer", token);
    return await http.SendAsync(req);
}
var legacyId = Guid.NewGuid();
{
    var v = await GetJson(Api + "/vault", regToken);
    var doc = JsonNode.Parse(S3.WebNowAccount.Open(vaultKey, Convert.FromBase64String(v["blob"]!.GetValue<string>()))!)!.AsObject();
    doc["s3Accounts"] = new JsonArray(new JsonObject
    {
        ["id"] = legacyId.ToString(), ["name"] = "CloudFly cũ", ["endpoint"] = "s3.cloudfly.vn", ["region"] = "us-east-1",
        ["accessKey"] = "AK_OLD", ["secretKey"] = "secret-cũ", ["useSsl"] = true, ["pathStyle"] = true,
        ["externalBuckets"] = new JsonArray(), ["createdAt"] = "2026-10-17T05:00:00Z", ["updatedAt"] = "2026-10-17T05:00:00Z"
    });
    var put = await PutJson(Api + "/vault", regToken, new { baseVersion = 1, blob = Convert.ToBase64String(S3.WebNowAccount.Seal(vaultKey, Encoding.UTF8.GetBytes(doc.ToJsonString()))) });
    Check(put.IsSuccessStatusCode, "Seed legacy s3Accounts in WNTerm vault (v2)");
}

// ----- WNS3Connect: đăng nhập, lần đầu → chuyển dữ liệu cũ sang ngăn riêng -----
var s3Settings = new S3.AppSettings { AccountApiUrl = Api };
var store = S3.AccountStore.Load();
store.ReplaceSynced(new(), new());
var cloud = new S3.WebNowAccount(s3Settings, store);
await cloud.LoginAsync(email, pw);
var s1 = await cloud.SyncAsync();
var mig = store.Accounts.SingleOrDefault();
Check(s1.Pushed && s1.ServerVersion == 1 && mig?.Id == legacyId && S3.SecretProtector.Decrypt(mig.SecretKeyEnc) == "secret-cũ",
      $"Migrate legacy account into own slot -> appvault v{s1.ServerVersion}");
var vAfter = await GetJson(Api + "/vault", regToken);
Check(vAfter["version"]!.GetValue<int>() == 2, "WNS3Connect did not write WNTerm's vault (still v2)");

// Thêm tài khoản → tự đẩy vào ngăn riêng
var accB = new S3Account { Name = "CloudFly control3", Endpoint = "s3.cloudfly.vn", AccessKey = "AK_B", SecretKeyEnc = S3.SecretProtector.Encrypt("secret-B") };
store.Upsert(accB);
var s2 = await cloud.SyncAsync();
Check(s2.Pushed && s2.ServerVersion == 2 && s2.Total == 2, $"Add account -> appvault v{s2.ServerVersion}, {s2.Total} accounts");

// WNTerm đổi VM nhiều lần → không ảnh hưởng ngăn của WNS3Connect
var list = wnSessions.Load(out _);
list.Add(new WNM.SessionInfo { Name = "db1", Host = "10.0.0.3", Username = "root" });
wnSessions.Save(list);
var r2 = await wn.SyncAsync();
Check(r2.Total == 3 && r2.ServerVersion == 3, $"WNTerm adds VM -> vault v{r2.ServerVersion}");
var s3 = await cloud.SyncAsync();
Check(!s3.Pushed && s3.Total == 2 && s3.Added + s3.Deleted + s3.Updated == 0 && s3.ServerVersion == 2, "WNS3 slot untouched by WNTerm writes (no change, v2)");
var r3 = await wn.SyncAsync();
Check(r3.Total == 3 && r3.Added + r3.Deleted == 0, "WNTerm still sees its 3 VM");

// Máy mới: xóa sạch local → đồng bộ → có lại đủ 2 tài khoản + secret
store.ReplaceSynced(new(), new());
var s4 = await cloud.SyncAsync();
Check(s4.Added == 2 && store.Accounts.Any(a => a.Id == accB.Id && S3.SecretProtector.Decrypt(a.SecretKeyEnc) == "secret-B"), "Restore on fresh PC from own slot");

// Xóa tài khoản cũ → máy mới không hồi sinh (dù vault chung của WNTerm vẫn còn bản cũ)
store.Remove(legacyId);
await cloud.SyncAsync();
store.ReplaceSynced(new(), new());
await cloud.SyncAsync();
Check(store.Accounts.Count == 1 && store.Accounts[0].Id == accB.Id, "Deleted account stays deleted (legacy copy ignored once slot exists)");

// Lịch sử phiên bản của ngăn riêng
var hist = await GetJson(Api + "/appvault/history?app=wns3connect", regToken);
Check(hist["versions"]!.AsArray().Count >= 3 && hist["current"]!.GetValue<int>() >= 3, $"appvault history: {hist["versions"]!.AsArray().Count} versions, current {hist["current"]}");

// API: 409 khi baseVersion cũ, tên app sai, app khác là ngăn khác, chưa đăng nhập
int cur = hist["current"]!.GetValue<int>();
var stale = await PutJson(Api + "/appvault", regToken, new { app = "wns3connect", baseVersion = cur - 1, blob = Convert.ToBase64String(new byte[40]) });
Check((int)stale.StatusCode == 409, $"Stale baseVersion -> {(int)stale.StatusCode}");
var bad = await PutJson(Api + "/appvault", regToken, new { app = "../x", baseVersion = 0, blob = Convert.ToBase64String(new byte[40]) });
Check((int)bad.StatusCode == 422, $"Bad app name -> {(int)bad.StatusCode}");
var other = await GetJson(Api + "/appvault?app=other-app", regToken);
Check(other["version"]!.GetValue<int>() == 0, "Different app = separate empty slot");
using (var anon = await http.GetAsync(Api + "/appvault?app=wns3connect"))
    Check((int)anon.StatusCode == 401, $"No token -> {(int)anon.StatusCode}");

// Dùng phiên đăng nhập WNTerm trên máy này
var cloud2 = new S3.WebNowAccount(new S3.AppSettings { AccountApiUrl = Api }, store);
await cloud2.UseWNTermLoginAsync();
var s5 = await cloud2.SyncAsync();
Check(cloud2.FromWNTerm && s5.Total == 1, "Borrow WNTerm login + sync own slot");
await cloud2.LogoutAsync();
var r4 = await wn.SyncAsync();
Check(r4.Total == 3, "Logout in WNS3 (borrowed token) keeps WNTerm logged in");

// Xóa tài khoản WebNow → dữ liệu ngăn riêng bị xóa theo (CASCADE)
using (var req = new HttpRequestMessage(HttpMethod.Post, Api + "/account/delete") { Content = JsonContent.Create(new { authKey = Convert.ToBase64String(authKey) }) })
{
    req.Headers.Authorization = new("Bearer", regToken);
    using var res = await http.SendAsync(req);
    Check(res.IsSuccessStatusCode, $"Delete WebNow account ({(int)res.StatusCode})");
}

try { Directory.Delete(root, true); } catch { }
Console.WriteLine(fail == 0 ? "ALL PASS" : $"{fail} FAILED");
return fail;
