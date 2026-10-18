using System.Security.Cryptography;
using Amazon.Runtime;

namespace WNS3Connect.Services;

/// <summary>
/// AWS SDK v4 chỉ gửi checksum kiểu mới (x-amz-checksum-crc32) cho DeleteObjects, không gửi Content-MD5.
/// Ceph (CloudFly) và nhiều S3-compatible cũ bắt buộc Content-MD5 → trả 400 và app phải xóa từng file (rất chậm).
/// Factory này cho SDK dùng HttpClient có thêm Content-MD5 vào mọi POST ?delete ngay trước khi gửi.
/// Content-MD5 không nằm trong SignedHeaders nên không ảnh hưởng chữ ký SigV4.
/// </summary>
public sealed class ContentMd5HttpClientFactory : HttpClientFactory
{
    public static readonly ContentMd5HttpClientFactory Instance = new();

    public override HttpClient CreateHttpClient(IClientConfig clientConfig)
    {
        var sockets = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = clientConfig.MaxConnectionsPerServer ?? 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            UseCookies = false,
            AllowAutoRedirect = false
        };
        return new HttpClient(new Md5Handler(sockets)) { Timeout = clientConfig.Timeout ?? TimeSpan.FromMinutes(5) };
    }

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => true;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    public override string GetConfigUniqueString(IClientConfig clientConfig) =>
        "wns3-md5:" + (clientConfig.MaxConnectionsPerServer ?? 32) + ":" + (clientConfig.Timeout?.TotalSeconds ?? 0);

    private sealed class Md5Handler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Environment.GetEnvironmentVariable("WNS3_NO_MD5") != "1" // chỉ để đo so sánh
                && request.Method == HttpMethod.Post && request.Content != null
                && request.Content.Headers.ContentMD5 == null
                && IsDeleteObjects(request.RequestUri))
            {
                byte[] body = await request.Content.ReadAsByteArrayAsync(ct);
                request.Content.Headers.ContentMD5 = MD5.HashData(body);
            }
            return await base.SendAsync(request, ct);
        }

        private static bool IsDeleteObjects(Uri? uri)
        {
            string q = uri?.Query ?? "";
            return q == "?delete" || q.StartsWith("?delete&", StringComparison.Ordinal) || q.Contains("&delete", StringComparison.Ordinal)
                   || q.StartsWith("?delete=", StringComparison.Ordinal);
        }
    }
}
