using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace WNS3Connect.Services.Restic;

/// <summary>Khóa của kho restic: AES-256-CTR để mã hóa, Poly1305-AES (k 16 byte + r 16 byte) để xác thực.</summary>
public sealed class ResticKey
{
    public required byte[] Encrypt { get; init; }   // 32 byte
    public required byte[] MacK { get; init; }      // 16 byte (khóa AES-128 cho nonce)
    public required byte[] MacR { get; init; }      // 16 byte

    public static ResticKey FromBytes(ReadOnlySpan<byte> b64) => new()
    {
        Encrypt = b64[..32].ToArray(),
        MacK = b64[32..48].ToArray(),
        MacR = b64[48..64].ToArray()
    };
}

public sealed class ResticWrongPasswordException() : Exception("Sai mật khẩu kho restic.");
public sealed class ResticIntegrityException(string what) : Exception("Dữ liệu restic bị hỏng hoặc sai khóa: " + what);

/// <summary>
/// Mật mã của restic (doc "References / Design"): file mã hóa = IV(16) ‖ AES-256-CTR(plaintext) ‖ Poly1305-AES(ciphertext, nonce=IV).
/// Khóa chính nằm trong keys/* được mã hóa bằng khóa sinh từ mật khẩu qua scrypt.
/// </summary>
public static class ResticCrypto
{
    public const int Overhead = 32;

    /// <summary>Giải mã + kiểm MAC. Sai MAC → ResticIntegrityException (hoặc sai mật khẩu nếu là file key).</summary>
    public static byte[] Decrypt(ResticKey key, ReadOnlySpan<byte> data, string what = "")
    {
        if (data.Length < Overhead) throw new ResticIntegrityException("file quá ngắn " + what);
        var iv = data[..16];
        var ct = data[16..^16];
        var mac = data[^16..];
        Span<byte> expect = stackalloc byte[16];
        PolyAes(key, iv, ct, expect);
        if (!CryptographicOperations.FixedTimeEquals(expect, mac)) throw new ResticIntegrityException("MAC không khớp " + what);
        var plain = new byte[ct.Length];
        AesCtr(key.Encrypt, iv, ct, plain);
        return plain;
    }

    /// <summary>Mã hóa (chỉ dùng cho test: tạo kho restic giả lập).</summary>
    public static byte[] Encrypt(ResticKey key, ReadOnlySpan<byte> plain)
    {
        var res = new byte[plain.Length + Overhead];
        RandomNumberGenerator.Fill(res.AsSpan(0, 16));
        AesCtr(key.Encrypt, res.AsSpan(0, 16), plain, res.AsSpan(16, plain.Length));
        PolyAes(key, res.AsSpan(0, 16), res.AsSpan(16, plain.Length), res.AsSpan(16 + plain.Length, 16));
        return res;
    }

    private static void PolyAes(ResticKey key, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> msg, Span<byte> tag)
    {
        Span<byte> pk = stackalloc byte[32];
        key.MacR.CopyTo(pk);
        using (var aes = Aes.Create())
        {
            aes.Key = key.MacK;
            aes.EncryptEcb(nonce, pk[16..], PaddingMode.None);
        }
        Poly1305(pk, msg, tag);
    }

    // ---------------- AES-256-CTR (bộ đếm 128-bit big-endian, như cipher.NewCTR của Go) ----------------

    public static void AesCtr(byte[] key, ReadOnlySpan<byte> iv, ReadOnlySpan<byte> input, Span<byte> output)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        const int Chunk = 64 * 1024;
        var counters = new byte[Math.Min(Chunk, (input.Length + 15) & ~15)];
        var stream = new byte[counters.Length];
        UInt128 ctr = BinaryPrimitives.ReadUInt128BigEndian(iv);
        for (int off = 0; off < input.Length; off += Chunk)
        {
            int n = Math.Min(Chunk, input.Length - off);
            int blocks = (n + 15) / 16;
            for (int b = 0; b < blocks; b++) { BinaryPrimitives.WriteUInt128BigEndian(counters.AsSpan(b * 16, 16), ctr); ctr++; }
            aes.EncryptEcb(counters.AsSpan(0, blocks * 16), stream.AsSpan(0, blocks * 16), PaddingMode.None);
            Xor(input.Slice(off, n), stream.AsSpan(0, n), output.Slice(off, n));
        }
    }

    private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> dst)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<byte>.Count;
            for (; i <= a.Length - w; i += w)
                (new Vector<byte>(a[i..]) ^ new Vector<byte>(b[i..])).CopyTo(dst[i..]);
        }
        for (; i < a.Length; i++) dst[i] = (byte)(a[i] ^ b[i]);
    }

    // ---------------- Poly1305 (poly1305-donna 32-bit) ----------------

    public static void Poly1305(ReadOnlySpan<byte> key, ReadOnlySpan<byte> m, Span<byte> tag)
    {
        static uint L(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s[o..]);
        const uint M26 = 0x3ffffff;
        uint r0 = L(key, 0) & 0x3ffffff, r1 = (L(key, 3) >> 2) & 0x3ffff03, r2 = (L(key, 6) >> 4) & 0x3ffc0ff,
             r3 = (L(key, 9) >> 6) & 0x3f03fff, r4 = (L(key, 12) >> 8) & 0x00fffff;
        uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
        uint h0 = 0, h1 = 0, h2 = 0, h3 = 0, h4 = 0;
        Span<byte> last = new byte[16];

        int pos = 0;
        while (pos < m.Length)
        {
            ReadOnlySpan<byte> blk;
            uint hibit;
            if (m.Length - pos >= 16) { blk = m.Slice(pos, 16); hibit = 1u << 24; pos += 16; }
            else
            {
                last.Clear();
                m[pos..].CopyTo(last);
                last[m.Length - pos] = 1;
                blk = last; hibit = 0; pos = m.Length;
            }
            h0 += L(blk, 0) & M26;
            h1 += (L(blk, 3) >> 2) & M26;
            h2 += (L(blk, 6) >> 4) & M26;
            h3 += (L(blk, 9) >> 6) & M26;
            h4 += (L(blk, 12) >> 8) | hibit;

            ulong d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
            ulong d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
            ulong d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
            ulong d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
            ulong d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

            ulong c = d0 >> 26; h0 = (uint)d0 & M26;
            d1 += c; c = d1 >> 26; h1 = (uint)d1 & M26;
            d2 += c; c = d2 >> 26; h2 = (uint)d2 & M26;
            d3 += c; c = d3 >> 26; h3 = (uint)d3 & M26;
            d4 += c; c = d4 >> 26; h4 = (uint)d4 & M26;
            h0 += (uint)c * 5; c = h0 >> 26; h0 &= M26;
            h1 += (uint)c;
        }

        uint cc = h1 >> 26; h1 &= M26;
        h2 += cc; cc = h2 >> 26; h2 &= M26;
        h3 += cc; cc = h3 >> 26; h3 &= M26;
        h4 += cc; cc = h4 >> 26; h4 &= M26;
        h0 += cc * 5; cc = h0 >> 26; h0 &= M26;
        h1 += cc;

        uint g0 = h0 + 5; cc = g0 >> 26; g0 &= M26;
        uint g1 = h1 + cc; cc = g1 >> 26; g1 &= M26;
        uint g2 = h2 + cc; cc = g2 >> 26; g2 &= M26;
        uint g3 = h3 + cc; cc = g3 >> 26; g3 &= M26;
        uint g4 = h4 + cc - (1u << 26);

        uint mask = (g4 >> 31) - 1;
        g0 &= mask; g1 &= mask; g2 &= mask; g3 &= mask; g4 &= mask;
        mask = ~mask;
        h0 = (h0 & mask) | g0; h1 = (h1 & mask) | g1; h2 = (h2 & mask) | g2; h3 = (h3 & mask) | g3; h4 = (h4 & mask) | g4;

        h0 = h0 | (h1 << 26);
        h1 = (h1 >> 6) | (h2 << 20);
        h2 = (h2 >> 12) | (h3 << 14);
        h3 = (h3 >> 18) | (h4 << 8);

        ulong f = (ulong)h0 + L(key, 16); h0 = (uint)f;
        f = (ulong)h1 + L(key, 20) + (f >> 32); h1 = (uint)f;
        f = (ulong)h2 + L(key, 24) + (f >> 32); h2 = (uint)f;
        f = (ulong)h3 + L(key, 28) + (f >> 32); h3 = (uint)f;

        BinaryPrimitives.WriteUInt32LittleEndian(tag, h0);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[4..], h1);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[8..], h2);
        BinaryPrimitives.WriteUInt32LittleEndian(tag[12..], h3);
    }

    // ---------------- scrypt (RFC 7914) ----------------

    public static byte[] Scrypt(byte[] password, byte[] salt, int N, int r, int p, int dkLen)
    {
        if (N < 2 || (N & (N - 1)) != 0) throw new ArgumentException("scrypt N phải là lũy thừa của 2");
        int blockLen = 128 * r;
        byte[] B = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1, HashAlgorithmName.SHA256, p * blockLen);
        // Các làn p độc lập nhau → chạy song song (restic thường dùng N=2^15, r=8, p=4+: mỗi làn ~32 MB).
        System.Threading.Tasks.Parallel.For(0, p, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 1, 8) }, i =>
        {
            var X = new uint[32 * r];
            var V = new uint[32 * r * N];
            var T = new uint[32 * r];
            var chunk = B.AsSpan(i * blockLen, blockLen);
            for (int k = 0; k < X.Length; k++) X[k] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(k * 4)..]);
            RoMix(X, V, T, N, r);
            for (int k = 0; k < X.Length; k++) BinaryPrimitives.WriteUInt32LittleEndian(chunk[(k * 4)..], X[k]);
        });
        return Rfc2898DeriveBytes.Pbkdf2(password, B, 1, HashAlgorithmName.SHA256, dkLen);
    }

    private static void RoMix(uint[] X, uint[] V, uint[] T, int N, int r)
    {
        int len = 32 * r;
        for (int i = 0; i < N; i++)
        {
            Array.Copy(X, 0, V, i * len, len);
            BlockMix(X, T, r);
        }
        for (int i = 0; i < N; i++)
        {
            int j = (int)(X[(2 * r - 1) * 16] & (uint)(N - 1));
            var vj = V.AsSpan(j * len, len);
            for (int k = 0; k < len; k++) X[k] ^= vj[k];
            BlockMix(X, T, r);
        }
    }

    private static void BlockMix(uint[] B, uint[] Y, int r)
    {
        Span<uint> x = stackalloc uint[16];
        B.AsSpan((2 * r - 1) * 16, 16).CopyTo(x);
        for (int i = 0; i < 2 * r; i++)
        {
            for (int k = 0; k < 16; k++) x[k] ^= B[i * 16 + k];
            Salsa8(x);
            // Khối chẵn về nửa đầu, khối lẻ về nửa sau.
            int dst = (i % 2 == 0 ? i / 2 : r + i / 2) * 16;
            x.CopyTo(Y.AsSpan(dst, 16));
        }
        Array.Copy(Y, B, 32 * r);
    }

    private static void Salsa8(Span<uint> b)
    {
        uint x0 = b[0], x1 = b[1], x2 = b[2], x3 = b[3], x4 = b[4], x5 = b[5], x6 = b[6], x7 = b[7],
             x8 = b[8], x9 = b[9], x10 = b[10], x11 = b[11], x12 = b[12], x13 = b[13], x14 = b[14], x15 = b[15];
        static uint R(uint a, int n) => BitOperations.RotateLeft(a, n);
        for (int i = 0; i < 8; i += 2)
        {
            x4 ^= R(x0 + x12, 7); x8 ^= R(x4 + x0, 9); x12 ^= R(x8 + x4, 13); x0 ^= R(x12 + x8, 18);
            x9 ^= R(x5 + x1, 7); x13 ^= R(x9 + x5, 9); x1 ^= R(x13 + x9, 13); x5 ^= R(x1 + x13, 18);
            x14 ^= R(x10 + x6, 7); x2 ^= R(x14 + x10, 9); x6 ^= R(x2 + x14, 13); x10 ^= R(x6 + x2, 18);
            x3 ^= R(x15 + x11, 7); x7 ^= R(x3 + x15, 9); x11 ^= R(x7 + x3, 13); x15 ^= R(x11 + x7, 18);
            x1 ^= R(x0 + x3, 7); x2 ^= R(x1 + x0, 9); x3 ^= R(x2 + x1, 13); x0 ^= R(x3 + x2, 18);
            x6 ^= R(x5 + x4, 7); x7 ^= R(x6 + x5, 9); x4 ^= R(x7 + x6, 13); x5 ^= R(x4 + x7, 18);
            x11 ^= R(x10 + x9, 7); x8 ^= R(x11 + x10, 9); x9 ^= R(x8 + x11, 13); x10 ^= R(x9 + x8, 18);
            x12 ^= R(x15 + x14, 7); x13 ^= R(x12 + x15, 9); x14 ^= R(x13 + x12, 13); x15 ^= R(x14 + x13, 18);
        }
        b[0] += x0; b[1] += x1; b[2] += x2; b[3] += x3; b[4] += x4; b[5] += x5; b[6] += x6; b[7] += x7;
        b[8] += x8; b[9] += x9; b[10] += x10; b[11] += x11; b[12] += x12; b[13] += x13; b[14] += x14; b[15] += x15;
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
    public static string Hex(ReadOnlySpan<byte> b) => Convert.ToHexStringLower(b);
    public static byte[] Sha256(ReadOnlySpan<byte> b) => SHA256.HashData(b);
}
