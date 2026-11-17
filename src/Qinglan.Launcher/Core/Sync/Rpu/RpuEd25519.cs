using System;
using System.Numerics;
using System.Security.Cryptography;

namespace Qinglan.Launcher.Core.Sync.Rpu;

/// <summary>
/// Ed25519 验签/签名（零依赖：BigInteger + SHA-512，RFC 8032 §5.1 完整流程）。
/// 验证公式（cofactorless）：[s]B == R + [r]A；解码点失败、s ≥ L 或坐标非法一律判 false。
/// 最大用途：RPU 发布清单信封验签（服务端 = JDK 标准实现，参见 ql-tmp 反逆向笔记）。
/// Sign 仅用于测试夹具构造假信封，生产路径只 Verify。
/// </summary>
public static class RpuEd25519
{
    private static readonly BigInteger Prime = (BigInteger.One << 255) - 19;
    private static readonly BigInteger Order = (BigInteger.One << 252) +
        BigInteger.Parse("27742317777372353535851937790883648493");
    private static readonly BigInteger D;
    private static readonly BigInteger D2;   // 2d（加法公式用）
    private static readonly BigInteger SqrtM1; // sqrt(-1) = 2^((p-1)/4)
    private static readonly Pt Base;

    static RpuEd25519()
    {
        var inv = BigInteger.ModPow(121666, Prime - 2, Prime);
        D = Mod((-121665 * inv) % Prime);
        D2 = Mod(D + D);
        SqrtM1 = BigInteger.ModPow(2, (Prime - 1) / 4, Prime);
        // 基点 B：y = 4/5，x 取偶数根（RFC 8032 的规范约定，不依赖硬编码字节）
        var y = Mod(4 * BigInteger.ModPow(5, Prime - 2, Prime) % Prime);
        Base = DecodePointUnchecked(y, even: true);
    }

    /// <summary> Ed25519 验签：msg 任意长（含 3MB 清单），签名 64 字节。 </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature64)
    {
        if (publicKey32.Length != 32 || signature64.Length != 64) return false;
        try
        {
            var a = DecodePoint(publicKey32);
            var r = DecodePoint(signature64[..32]);
            var s = Le(signature64[32..]);
            if (s >= Order) return false;
            var hash = SHA512.HashData(signature64[..32].ToArray().Concat(publicKey32.ToArray()).Concat(message.ToArray()).ToArray());
            var rScalar = Le(hash) % Order;
            return Eq(Mul(s, Base), Add(r, Mul(rScalar, a)));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary> Ed25519 签名（测试/工具用）：seed 32B → 64B 签名；与 JDK 实现逐字节兼容。 </summary>
    public static byte[] Sign(ReadOnlySpan<byte> seed32, ReadOnlySpan<byte> message)
    {
        if (seed32.Length != 32) throw new ArgumentException("seed 必须 32 字节", nameof(seed32));
        var h = SHA512.HashData(seed32.ToArray());
        var seedBytes = (byte[])h[..32].Clone();
        seedBytes[0] &= 248; seedBytes[31] &= 127; seedBytes[31] |= 64;   // clamp
        var a = Le(seedBytes);
        var pub = Encode(Mul(a, Base));
        var h2 = SHA512.HashData(h[32..].Concat(message.ToArray()).ToArray());
        var r = Le(h2) % Order;
        var rEnc = Encode(Mul(r, Base));
        var h3 = SHA512.HashData(rEnc.Concat(pub).Concat(message.ToArray()).ToArray());
        var k = Le(h3) % Order;
        var s = (r + k * a) % Order;
        var outBytes = new byte[64];
        rEnc.CopyTo(outBytes, 0);
        Le32(outBytes.AsSpan(32), s);
        return outBytes;
    }

    /// <summary> Ed25519 公钥推导（测试用）：seed 32B → 公钥 32B（与 JDK PKCS8 语义一致）。 </summary>
    public static byte[] PublicKeyFromSeed(ReadOnlySpan<byte> seed32)
    {
        var h = SHA512.HashData(seed32.ToArray());
        var seedBytes = (byte[])h[..32].Clone();
        seedBytes[0] &= 248; seedBytes[31] &= 127; seedBytes[31] |= 64;
        return Encode(Mul(Le(seedBytes), Base));
    }

    // ───────────────────── 编解码 ─────────────────────

    /// <summary>解码 32 字节压缩 Public 点（RFC 8032 §5.1.3 的 y 恢复 + x 开方 + 奇偶校正）。失败抛 FormatException。</summary>
    private static Pt DecodePoint(ReadOnlySpan<byte> bytes32)
    {
        var b = bytes32.ToArray();
        int sign = (b[31] >> 7) & 1;
        b[31] &= 0x7F;
        var y = Le(b);
        if (y >= Prime) throw new FormatException("点坐标超出域");
        return DecodePointUnchecked(y, sign != 1);
    }

    /// <summary>已知 y 与“x 取偶/奇”约定，恢复点。root 不存在时抛 FormatException。</summary>
    private static Pt DecodePointUnchecked(BigInteger y, bool even)
    {
        if (y < 0 || y >= Prime) throw new FormatException("y 不在域内");
        var u = Mod(y * y - 1);
        var v = Mod(D * y * y + 1);
        // 专用公式（p ≡ 5 mod 8）：x = u·v³·(u·v⁷)^((p-5)/8)
        var x = Mod(u * BigInteger.ModPow(v, 3, Prime) % Prime *
                    BigInteger.ModPow(Mod(u * BigInteger.ModPow(v, 7, Prime)), (Prime - 5) / 8, Prime));
        var vx2 = Mod(v * Mod(x * x));
        if (vx2 == Mod(Prime - u))
            x = Mod(x * SqrtM1);            // x²v == -u：乘 sqrt(-1) 翻面
        else if (vx2 != u)
            throw new FormatException("u/v 非二次剩余，点不存在");
        if (x.IsZero && even == false) throw new FormatException("x=0 且带奇号");
        var parity = x.IsEven ? 0 : 1;
        if (parity != (even ? 0 : 1))
            x = Mod(Prime - x);
        return new Pt(Mod(x), y, BigInteger.One, Mod(x * y));
    }

    /// <summary>压缩编码 32 字节：LE(y) + 最高字节带 x 的奇偶符号位。</summary>
    private static byte[] Encode(Pt p)
    {
        var inv = BigInteger.ModPow(p.Z, Prime - 2, Prime);
        var x = Mod(p.X * inv % Prime);
        var y = Mod(p.Y * inv % Prime);
        var b = new byte[32];
        Le32(b, y);
        if (!x.IsEven) b[31] |= 0x80;
        return b;
    }

    // ───────────────────── 点运算（twisted Edwards, a = -1，扩展坐标）─────────────────────

    private readonly record struct Pt(BigInteger X, BigInteger Y, BigInteger Z, BigInteger T);

    private static Pt Identity => new(BigInteger.Zero, BigInteger.One, BigInteger.One, BigInteger.Zero);

    private static Pt Add(Pt u, Pt v)
    {
        // twisted Edwards (a=-1) 齐化加法律（自洽推导，B+O=B 已实测验证）：
        // x3=(X1Y2+X2Y1)/(Z1Z2+dT1T2), y3=(Y1Y2+X1X2)/(Z1Z2-dT1T2)
        var a = Mod(u.X * v.X);
        var b = Mod(u.Y * v.Y);
        var c = Mod(D * u.T * v.T);
        var d = Mod(u.Z * v.Z);
        var e = Mod(Mod((u.X + u.Y) * (v.X + v.Y)) - a - b);   // = X1Y2 + X2Y1
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return new Pt(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    private static Pt Double(Pt p) => Add(p, p);

    private static Pt Mul(BigInteger k, Pt p)
    {
        var r = Identity;
        var q = p;
        int bits = k.IsZero ? 0 : (int)k.GetBitLength();
        for (int i = 0; i < bits; i++)
        {
            if (((k >> i) & BigInteger.One) == BigInteger.One) r = Add(r, q);
            q = Double(q);
        }
        return r;
    }

    /// <summary>射影相等（X/Z 与 Y/Z 双分量交叉相等）。</summary>
    private static bool Eq(Pt u, Pt v)
        => Mod(u.X * v.Z) == Mod(v.X * u.Z) && Mod(u.Y * v.Z) == Mod(v.Y * u.Z);

    // ───────────────────── 字节工具 ─────────────────────

    private static BigInteger Mod(BigInteger v)
    {
        v %= Prime;
        return v.Sign < 0 ? v + Prime : v;
    }

    /// <summary>小端整数（符号安全：逐字节 Horner 构造非负值）。</summary>
    private static BigInteger Le(ReadOnlySpan<byte> bytes)
    {
        var v = BigInteger.Zero;
        for (int i = bytes.Length - 1; i >= 0; i--)
            v = (v << 8) | bytes[i];
        return v;
    }

    /// <summary>值 → 恰 32 字节小端（值必须 < 2^256）。</summary>
    private static void Le32(Span<byte> target, BigInteger value)
    {
        for (int i = 0; i < 32; i++)
        {
            target[i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }
}
