namespace WNS3Connect.Models;

/// <summary>Một dòng trong danh sách file: thư mục (CommonPrefix) hoặc object.</summary>
public sealed class S3Entry
{
    public required string Key { get; init; }
    public bool IsFolder { get; init; }
    public long Size { get; init; }
    public DateTime? LastModified { get; init; }
    public string StorageClass { get; init; } = "";
    public string ETag { get; init; } = "";

    /// <summary>Tên hiển thị (phần cuối của key, thư mục có dấu "/").</summary>
    public string Name
    {
        get
        {
            string k = IsFolder ? Key.TrimEnd('/') : Key;
            int i = k.LastIndexOf('/');
            string n = i >= 0 ? k[(i + 1)..] : k;
            return IsFolder ? n + "/" : n;
        }
    }

    public override string ToString() => Name;

    public string SizeText => IsFolder ? "" : FormatSize(Size);

    public string TypeText
    {
        get
        {
            if (IsFolder) return "Thư mục";
            string n = Name;
            int i = n.LastIndexOf('.');
            return i > 0 && i < n.Length - 1 ? n[(i + 1)..].ToUpperInvariant() : "File";
        }
    }

    /// <summary>Màu icon theo loại file (ảnh, nén, tài liệu, code, media, dữ liệu).</summary>
    public string IconColor
    {
        get
        {
            if (IsFolder) return "#F5B83D";
            string ext = TypeText.ToLowerInvariant();
            return ext switch
            {
                "jpg" or "jpeg" or "png" or "gif" or "webp" or "svg" or "bmp" or "ico" or "avif" => "#12B76A",
                "zip" or "gz" or "tgz" or "tar" or "rar" or "7z" or "zst" or "bz2" or "xz" => "#7A5AF8",
                "pdf" => "#F04438",
                "doc" or "docx" or "xls" or "xlsx" or "csv" or "ppt" or "pptx" or "txt" or "md" or "log" => "#2E90FA",
                "js" or "ts" or "php" or "html" or "htm" or "css" or "json" or "xml" or "yml" or "yaml" or "sh" or "py" or "cs" => "#F79009",
                "mp4" or "mov" or "mkv" or "avi" or "webm" or "mp3" or "wav" or "m4a" => "#EE46BC",
                "sql" or "db" or "sqlite" or "bak" or "dump" => "#0BA5EC",
                _ => "#98A2B3"
            };
        }
    }

    public string LastModifiedText => LastModified?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "";

    public static string FormatSize(long b)
    {
        if (b < 1024) return $"{b} B";
        double v = b;
        string[] u = { "KB", "MB", "GB", "TB", "PB" };
        int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return v >= 100 ? $"{v:0} {u[i]}" : v >= 10 ? $"{v:0.0} {u[i]}" : $"{v:0.00} {u[i]}";
    }
}
