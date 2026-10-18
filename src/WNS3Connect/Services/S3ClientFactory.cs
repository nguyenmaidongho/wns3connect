using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using WNS3Connect.Models;

namespace WNS3Connect.Services;

public static class S3ClientFactory
{
    public static string StripScheme(string endpoint)
    {
        string e = (endpoint ?? "").Trim();
        if (e.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) e = e[8..];
        else if (e.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) e = e[7..];
        return e.TrimEnd('/');
    }

    public static AmazonS3Client Create(S3Account a, string? secretOverride = null)
    {
        var cfg = new AmazonS3Config
        {
            ForcePathStyle = a.PathStyle,
            // Nhiều dịch vụ S3-compatible (Ceph, MinIO cũ...) không hiểu checksum CRC mới của SDK v4.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            Timeout = TimeSpan.FromMinutes(5),
            MaxErrorRetry = 3,
            // Mở nhiều kết nối song song tới cùng host (upload/download nhiều file + list cùng lúc).
            MaxConnectionsPerServer = 32,
            // Thêm Content-MD5 cho DeleteObjects (Ceph/CloudFly bắt buộc) → xóa 1000 file / request.
            HttpClientFactory = ContentMd5HttpClientFactory.Instance
        };

        string region = string.IsNullOrWhiteSpace(a.Region) ? "us-east-1" : a.Region.Trim();
        string host = StripScheme(a.Endpoint);
        if (host.Length > 0)
        {
            cfg.ServiceURL = (a.UseSsl ? "https://" : "http://") + host;
            cfg.AuthenticationRegion = region;
        }
        else
        {
            cfg.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            cfg.UseHttp = !a.UseSsl;
        }

        string secret = secretOverride ?? SecretProtector.Decrypt(a.SecretKeyEnc) ?? "";
        return new AmazonS3Client(new BasicAWSCredentials(a.AccessKey.Trim(), secret), cfg);
    }

    /// <summary>Thông báo lỗi dễ hiểu từ exception của SDK.</summary>
    public static string Describe(Exception ex)
    {
        if (ex is OperationCanceledException) return "Đã hủy.";
        if (ex is AmazonS3Exception s3)
        {
            string code = s3.ErrorCode ?? "";
            return code switch
            {
                "InvalidAccessKeyId" => "Access Key ID không đúng.",
                "SignatureDoesNotMatch" => "Secret Access Key không đúng (chữ ký không khớp).",
                "AccessDenied" => "Không có quyền (AccessDenied).",
                "NoSuchBucket" => "Bucket không tồn tại.",
                "NoSuchKey" => "File không tồn tại.",
                "BucketNotEmpty" => "Bucket chưa rỗng — xóa hết file trước.",
                "BucketAlreadyExists" or "BucketAlreadyOwnedByYou" => "Tên bucket đã có người dùng.",
                "InvalidBucketName" => "Tên bucket không hợp lệ (chỉ chữ thường, số, dấu - và .).",
                _ => $"{(code.Length > 0 ? code + ": " : "")}{s3.Message} (HTTP {(int)s3.StatusCode})"
            };
        }
        if (ex is AmazonServiceException svc) return svc.Message;
        if (ex is HttpRequestException http) return "Không kết nối được tới máy chủ: " + (http.InnerException?.Message ?? http.Message);
        return ex.InnerException != null && ex is AmazonClientException ? ex.InnerException.Message : ex.Message;
    }
}
