using System.Text.Json;
using System.Text.Json.Serialization;
using WNS3Connect.Models;

namespace WNS3Connect.Services;

/// <summary>Danh sách tài khoản S3 lưu ở %APPDATA%\WNS3Connect\accounts.json (Secret Key mã hóa DPAPI).</summary>
public class AccountStore
{
    private class FileModel
    {
        [JsonPropertyName("accounts")] public List<S3Account> Accounts { get; set; } = new();
        [JsonPropertyName("deleted")] public List<S3AccountTombstone> Deleted { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public List<S3Account> Accounts { get; private set; } = new();
    public List<S3AccountTombstone> Deleted { get; private set; } = new();

    /// <summary>Phát sau mỗi thay đổi do người dùng (để tự đồng bộ lên tài khoản WebNow).</summary>
    public event Action? ChangedByUser;

    public static AccountStore Load()
    {
        var s = new AccountStore();
        try
        {
            if (File.Exists(AppPaths.AccountsFile))
            {
                var m = JsonSerializer.Deserialize<FileModel>(File.ReadAllText(AppPaths.AccountsFile)) ?? new();
                s.Accounts = m.Accounts;
                s.Deleted = m.Deleted;
            }
        }
        catch
        {
            // File hỏng: giữ lại một bản để còn cứu, bắt đầu lại từ rỗng.
            try { File.Copy(AppPaths.AccountsFile, AppPaths.AccountsFile + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); } catch { }
        }
        return s;
    }

    public void Save()
    {
        var cutoff = DateTime.UtcNow.AddDays(-180);
        Deleted.RemoveAll(t => t.At < cutoff);
        AppPaths.WriteAllTextAtomic(AppPaths.AccountsFile, JsonSerializer.Serialize(new FileModel { Accounts = Accounts, Deleted = Deleted }, Json));
    }

    public void Upsert(S3Account a)
    {
        a.UpdatedAt = DateTime.UtcNow;
        int i = Accounts.FindIndex(x => x.Id == a.Id);
        if (i >= 0) Accounts[i] = a; else Accounts.Add(a);
        Deleted.RemoveAll(t => t.Id == a.Id);
        Save();
        ChangedByUser?.Invoke();
    }

    public void Remove(Guid id)
    {
        var a = Accounts.FirstOrDefault(x => x.Id == id);
        if (a == null) return;
        Accounts.Remove(a);
        Deleted.RemoveAll(t => t.Id == id);
        Deleted.Add(new S3AccountTombstone { Id = id, At = DateTime.UtcNow, Name = a.DisplayName });
        Save();
        ChangedByUser?.Invoke();
    }

    /// <summary>Thay toàn bộ (sau khi gộp với dữ liệu đồng bộ), không phát ChangedByUser.</summary>
    public void ReplaceSynced(List<S3Account> accounts, List<S3AccountTombstone> deleted)
    {
        Accounts = accounts;
        Deleted = deleted;
        Save();
    }

    /// <summary>Đọc cấu hình "Cloud backup" S3 trong WNTerm trên máy này (nếu có).</summary>
    public static S3Account? ReadFromWNTerm()
    {
        try
        {
            string file = Path.Combine(AppPaths.WNTermDataDir, "settings.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            string Get(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            string ak = Get("S3AccessKey");
            if (string.IsNullOrWhiteSpace(ak)) return null;
            string? secret = SecretProtector.Decrypt(Get("S3SecretKeyEnc"), SecretProtector.WNTermEntropy);
            string endpoint = Get("S3Endpoint").Trim();
            bool ssl = !endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
            endpoint = S3ClientFactory.StripScheme(endpoint);
            var bucket = Get("S3Bucket").Trim();
            return new S3Account
            {
                Name = "WNTerm backup" + (endpoint.Length > 0 ? " - " + endpoint : ""),
                Endpoint = endpoint,
                Region = string.IsNullOrWhiteSpace(Get("S3Region")) ? "us-east-1" : Get("S3Region"),
                AccessKey = ak.Trim(),
                SecretKeyEnc = SecretProtector.Encrypt(secret),
                UseSsl = ssl,
                PathStyle = !r.TryGetProperty("S3PathStyle", out var ps) || ps.ValueKind != JsonValueKind.False,
                ExternalBuckets = bucket.Length > 0 ? new() { bucket } : new()
            };
        }
        catch { return null; }
    }
}
