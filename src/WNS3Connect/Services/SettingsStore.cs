using System.Text.Json;

namespace WNS3Connect.Services;

public class AppSettings
{
    /// <summary>light | dark | system</summary>
    public string Theme { get; set; } = "light";
    public Guid? LastAccountId { get; set; }
    public string? LastBucket { get; set; }
    public string? DownloadFolder { get; set; }
    /// <summary>Số file truyền song song.</summary>
    public int ParallelTransfers { get; set; } = 4;
    public double WindowWidth { get; set; } = 990;
    public double WindowHeight { get; set; } = 684;
    public double SidebarWidth { get; set; } = 230;
    public bool SidebarCollapsed { get; set; }
    /// <summary>Đánh dấu đã áp các thay đổi bố cục một lần (2 = thu nhỏ cửa sổ 10%).</summary>
    public int LayoutVersion { get; set; }
    public bool WindowMaximized { get; set; }

    // Tài khoản WebNow (chung với WNTerm, https://wnterm.webnow.vn)
    public string AccountApiUrl { get; set; } = "";
    public string? AccountEmail { get; set; }
    public string? AccountTokenEnc { get; set; }
    public string? AccountVaultKeyEnc { get; set; }
    /// <summary>Token mượn từ WNTerm trên máy này → đăng xuất ở đây không được gọi /logout (sẽ đá luôn WNTerm).</summary>
    public bool AccountTokenFromWNTerm { get; set; }
    public int AccountVaultVersion { get; set; }
    public DateTime? AccountLastSyncUtc { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new();
        }
        catch { }
        return new();
    }

    public static void Save(AppSettings s)
    {
        try { AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, JsonSerializer.Serialize(s, Json)); }
        catch { }
    }
}
