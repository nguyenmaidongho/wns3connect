using System.Security.Cryptography;
using System.Text;

namespace WNS3Connect.Services;

/// <summary>Mã hóa bí mật (Secret Key...) bằng DPAPI theo user Windows hiện tại.</summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WNS3Connect.DPAPI.Entropy.v1");

    /// <summary>Entropy WNTerm dùng — để đọc cấu hình S3 / phiên đăng nhập WNTerm trên cùng máy.</summary>
    internal static readonly byte[] WNTermEntropy = Encoding.UTF8.GetBytes("WNTerm.DPAPI.Entropy.v1");

    public static string? Encrypt(string? plain) => Encrypt(plain, Entropy);

    public static string? Decrypt(string? cipher) => Decrypt(cipher, Entropy);

    internal static string? Encrypt(string? plain, byte[] entropy)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        byte[] c = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(c);
    }

    internal static string? Decrypt(string? cipher, byte[] entropy)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try
        {
            byte[] p = ProtectedData.Unprotect(Convert.FromBase64String(cipher), entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(p);
        }
        catch { return null; }
    }
}
