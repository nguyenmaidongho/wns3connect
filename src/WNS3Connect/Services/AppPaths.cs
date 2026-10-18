namespace WNS3Connect.Services;

public static class AppPaths
{
    public static string DataDir { get; } = Ensure(Environment.GetEnvironmentVariable("WNS3_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WNS3Connect"));

    public static string LocalDir { get; } = Ensure(Environment.GetEnvironmentVariable("WNS3_LOCAL_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WNS3Connect"));

    public static string AccountsFile => Path.Combine(DataDir, "accounts.json");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogsDir { get; } = Ensure(Path.Combine(LocalDir, "logs"));
    public static string TempDir { get; } = Ensure(Path.Combine(LocalDir, "open"));

    /// <summary>Thư mục dữ liệu của WNTerm (để nhập cấu hình S3 / dùng chung đăng nhập).</summary>
    public static string WNTermDataDir => Environment.GetEnvironmentVariable("WNTERM_DATA_DIR")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WNTerm");

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Ghi file kiểu "ghi ra file tạm rồi đổi tên" để không bao giờ để lại file hỏng khi app tắt ngang.</summary>
    public static void WriteAllTextAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
