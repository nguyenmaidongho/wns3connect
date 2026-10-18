using System.Text.Json.Serialization;

namespace WNS3Connect.Models;

/// <summary>Một tài khoản S3 / S3-compatible (CloudFly, AWS, R2, MinIO, Wasabi...).</summary>
public class S3Account
{
    [JsonPropertyName("id")] public Guid Id { get; set; } = Guid.NewGuid();
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    /// <summary>Host hoặc URL, vd "s3.cloudfly.vn". Trống = Amazon S3 theo Region.</summary>
    [JsonPropertyName("endpoint")] public string Endpoint { get; set; } = "";
    [JsonPropertyName("region")] public string Region { get; set; } = "us-east-1";
    [JsonPropertyName("accessKey")] public string AccessKey { get; set; } = "";
    /// <summary>Secret Key đã mã hóa DPAPI (chỉ máy/user này đọc được).</summary>
    [JsonPropertyName("secretKeyEnc")] public string? SecretKeyEnc { get; set; }
    [JsonPropertyName("useSsl")] public bool UseSsl { get; set; } = true;
    [JsonPropertyName("pathStyle")] public bool PathStyle { get; set; } = true;
    /// <summary>Bucket thêm tay (khi key không có quyền ListBuckets).</summary>
    [JsonPropertyName("externalBuckets")] public List<string> ExternalBuckets { get; set; } = new();
    /// <summary>Mật khẩu các kho restic (WebNowBackup) trong tài khoản này — mã hóa DPAPI, đồng bộ E2E như Secret Key.</summary>
    [JsonPropertyName("resticRepos")] public List<ResticRepoRef> ResticRepos { get; set; } = new();
    [JsonPropertyName("createdAt")] public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("updatedAt")] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name) ? (string.IsNullOrWhiteSpace(Endpoint) ? "Amazon S3" : Endpoint) : Name;

    public S3Account Clone() => new()
    {
        Id = Id, Name = Name, Endpoint = Endpoint, Region = Region, AccessKey = AccessKey, SecretKeyEnc = SecretKeyEnc,
        UseSsl = UseSsl, PathStyle = PathStyle, ExternalBuckets = new(ExternalBuckets),
        ResticRepos = ResticRepos.Select(r => r.Clone()).ToList(), CreatedAt = CreatedAt, UpdatedAt = UpdatedAt
    };
}

/// <summary>Kho restic đã lưu mật khẩu: bucket + thư mục kho (có "/" ở cuối).</summary>
public class ResticRepoRef
{
    [JsonPropertyName("bucket")] public string Bucket { get; set; } = "";
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("passwordEnc")] public string? PasswordEnc { get; set; }
    [JsonPropertyName("savedAt")] public DateTime SavedAt { get; set; } = DateTime.UtcNow;

    public ResticRepoRef Clone() => new() { Bucket = Bucket, Path = Path, PasswordEnc = PasswordEnc, SavedAt = SavedAt };
}

/// <summary>Dấu xóa (để khi đồng bộ, máy khác cũng xóa tài khoản này).</summary>
public class S3AccountTombstone
{
    [JsonPropertyName("id")] public Guid Id { get; set; }
    [JsonPropertyName("at")] public DateTime At { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}
