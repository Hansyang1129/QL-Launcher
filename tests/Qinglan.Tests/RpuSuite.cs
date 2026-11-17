using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Sync.Rpu;

namespace Qinglan.Tests;

/// <summary>RPU 协议全家桶：Ed25519 向量、线上信封回归、分段对象、清单校验、缓存、客户端错误路径、引擎端到端。</summary>
internal static class RpuSuite
{
    /// <summary>测试签名种子（与生产部署密钥无关）。</summary>
    private static readonly byte[] TestSeed = Enumerable.Repeat((byte)0x5A, 32).ToArray();
    /// <summary>测试对象加密密钥（32B AES-GCM）。</summary>
    private static readonly byte[] TestObjKey = Enumerable.Repeat((byte)0xC3, 32).ToArray();
    private static readonly byte[] TestNonce = [1, 2, 3, 4, 5, 6, 7, 8];

    public static void Run()
    {
        T.Group("RPU · Ed25519 RFC/JDK 向量");
        Ed25519Vectors();
        T.Group("RPU · 线上信封回归（探针实测数据）");
        LiveEnvelope();
        T.Group("RPU · 分段对象往返");
        SegmentRoundTrip();
        T.Group("RPU · 分段对象破坏检测");
        SegmentTamper();
        T.Group("RPU · 清单校验与路径");
        ManifestValidation();
        T.Group("RPU · 缓存锁与快照提交");
        CacheLockCommit();
        T.Group("RPU · 缓存对象校验与清理");
        CacheObjectsCleanup();
        T.Group("RPU · 部署配置");
        DeploymentConfig();
        T.Group("RPU · 客户端形状与重试");
        ClientShape().GetAwaiter().GetResult();
        T.Group("RPU · 客户端错误文案");
        ClientErrors().GetAwaiter().GetResult();
        T.Group("RPU · 引擎全链路（假服务器）");
        EngineEndToEnd().GetAwaiter().GetResult();
    }

    // ────────────────────────────── 工具 ──────────────────────────────

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
    private static string Sha256Hex(byte[] data) => Hex(SHA256.HashData(data));

    private static bool ThrowsRpu(Action action, string needle)
    {
        try { action(); return false; }
        catch (RpuException e) { return e.Message.Contains(needle, StringComparison.Ordinal); }
        catch { return false; }
    }

    private static async Task CheckRpuAsync(string name, string needle, Func<Task> action)
    {
        try { await action().ConfigureAwait(false); T.Check(name + "（未抛出）", false); }
        catch (RpuException e) { T.Check(name, e.Message.Contains(needle, StringComparison.Ordinal)); }
        catch (Exception e) { T.Check(name + "（非预期异常 " + e.GetType().Name + ": " + e.Message + "）", false); }
    }

    /// <summary>预期不抛：任何异常都算失败。</summary>
    private static bool NotThrows(Action action)
    {
        try { action(); return true; }
        catch { return false; }
    }

    /// <summary>按 QRPUOBJ1 规格独立构造加密对象（与生产实现解耦的参照实现）。</summary>
    private static byte[] BuildObject(string objectIdHex, byte[] plain, byte[] key32, byte[] noncePrefix)
    {
        const int segSize = 1048576;
        int segs = Math.Max(1, (plain.Length + segSize - 1) / segSize);
        var header = new byte[68];
        Encoding.ASCII.GetBytes("QRPUOBJ1").CopyTo(header, 0);
        WriteBE(header, 8, 4, 1);
        Convert.FromHexString(objectIdHex).CopyTo(header, 12);
        WriteBE(header, 44, 8, plain.Length);
        WriteBE(header, 52, 4, segSize);
        WriteBE(header, 56, 4, segs);
        noncePrefix.CopyTo(header, 60);

        using var ms = new MemoryStream();
        ms.Write(header);
        using var aes = new AesGcm(key32, 16);
        for (int s = 0; s < segs; s++)
        {
            int len = Math.Min(segSize, plain.Length - s * segSize);
            var aad = new byte[76];
            header.CopyTo(aad, 0);
            WriteBE(aad, 68, 4, s);
            WriteBE(aad, 72, 4, len);
            var iv = new byte[12];
            noncePrefix.CopyTo(iv, 0);
            WriteBE(iv, 8, 4, s);
            var ct = new byte[len];
            var tag = new byte[16];
            aes.Encrypt(iv, plain.AsSpan(s * segSize, len), ct, tag, aad);
            ms.Write(ct);
            ms.Write(tag);
        }
        return ms.ToArray();
    }

    private static void WriteBE(byte[] dst, int offset, int size, long value)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            dst[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    private static string ObjectIdOf(int n) => Sha256Hex(Encoding.UTF8.GetBytes("rpu-object-" + n));

    private static (RpuFileEntry Entry, byte[] Cipher, byte[] Plain) MakeObject(int n, int plainSize)
    {
        var plain = new byte[plainSize];
        new Random(n * 7919).NextBytes(plain);
        var id = ObjectIdOf(n);
        var cipher = BuildObject(id, plain, TestObjKey, TestNonce);
        var entry = new RpuFileEntry("content/obj-" + n + ".json", id, plain.Length, cipher.Length,
            Sha256Hex(plain), Sha256Hex(cipher));
        return (entry, cipher, plain);
    }

    private static RpuManifest TestManifest(string releaseId, params RpuFileEntry[] files)
        => new(1, releaseId, new[] { new RpuPack("pack1", "测试资源包", "用于测试", null, files) });

    /// <summary>用测试种子给清单封一份信封（JSON 形态与服务端一致）。</summary>
    private static string Seal(RpuManifest manifest, byte[] seed)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest)));
        var signature = Convert.ToBase64String(RpuEd25519.Sign(seed, Convert.FromBase64String(payload)));
        return JsonSerializer.Serialize(new RpuEnvelope(payload, signature));
    }

    // ────────────────────────────── Ed25519 ──────────────────────────────

    private static void Ed25519Vectors()
    {
        const string seedHex = "9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60";
        const string pubHex = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a";
        const string sig0Hex = "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b";
        const string sig1Hex = "6c03859d172af8636ed550a67bac563d6b19eb83756e2f55054430d76a5491b9102434b774fa225a131966c875201175592796f00db413af7782d37aee857700";
        const string sig2Hex = "71b724f3fb660ca7a520e12a432a0cd3f8fd364454793454797fcfa77564e6a4d06b94048919943fe001dd5ac5c08cea4f2754085f49c6068f9a8f1b35344102";
        var seed = Convert.FromHexString(seedHex);
        var pub = Convert.FromHexString(pubHex);

        T.CheckEq("RFC8032 TEST1 公钥推导", pubHex, Hex(RpuEd25519.PublicKeyFromSeed(seed)));
        T.CheckEq("RFC8032 TEST1 空消息签名", sig0Hex, Hex(RpuEd25519.Sign(seed, Array.Empty<byte>())));
        T.Check("RFC8032 TEST1 验签通过",
            RpuEd25519.Verify(pub, Array.Empty<byte>(), Convert.FromHexString(sig0Hex)));

        var msg1 = Encoding.UTF8.GetBytes("qinglan-rpu-payload-1");
        var msg2 = Encoding.UTF8.GetBytes("qinglan-rpu-payload-2-longer-test");
        T.CheckEq("JDK 确定性向量 SIG1", sig1Hex, Hex(RpuEd25519.Sign(seed, msg1)));
        T.CheckEq("JDK 确定性向量 SIG2", sig2Hex, Hex(RpuEd25519.Sign(seed, msg2)));
        T.Check("JDK 向量 SIG1 验签通过", RpuEd25519.Verify(pub, msg1, Convert.FromHexString(sig1Hex)));
        T.Check("JDK 向量 SIG2 验签通过", RpuEd25519.Verify(pub, msg2, Convert.FromHexString(sig2Hex)));

        var tamperedSig = (byte[])Convert.FromHexString(sig1Hex).Clone();
        tamperedSig[10] ^= 0xFF;
        T.Check("签名位翻转 → 拒绝", !RpuEd25519.Verify(pub, msg1, tamperedSig));
        T.Check("消息被篡改 → 拒绝", !RpuEd25519.Verify(pub, msg2, Convert.FromHexString(sig1Hex)));
        T.Check("错误公钥 → 拒绝",
            !RpuEd25519.Verify(RpuEd25519.PublicKeyFromSeed(Enumerable.Repeat((byte)0x11, 32).ToArray()),
                msg1, Convert.FromHexString(sig1Hex)));
    }

    private static void LiveEnvelope()
    {
        var envPath = @"%TEMP%\ql-tmp\rpu-live\envelope-live.json";
        var depPath = @"%TEMP%\ql-tmp\rpu-deployment.json";
        if (!File.Exists(envPath) || !File.Exists(depPath))
        {
            T.Check("线上信封回归（本地探针产物缺失，跳过）", true);
            return;
        }
        var envJson = File.ReadAllText(envPath);
        using var dep = JsonDocument.Parse(File.ReadAllText(depPath));
        var key = RpuDeploymentConfig.DecodeSigningKey(dep.RootElement.GetProperty("signingPublicKey").GetString()!);
        var releaseId = RpuProtocol.TryEnvelopeReleaseId(envJson);
        T.Check("线上快照 releaseId 可提取", !string.IsNullOrEmpty(releaseId));
        var envelope = JsonSerializer.Deserialize<RpuEnvelope>(envJson);
        var manifest = RpuProtocol.VerifyAndParseManifest(envelope!, releaseId!, key);
        T.Check("线上信封以部署公钥验签通过", manifest.ReleaseId == releaseId && manifest.Packs.Count > 0);
        T.Check("线上清单规模在量级（>1000 文件）", manifest.Packs.Sum(p => p.Files.Count) > 1000);
    }

    // ────────────────────────────── 分段对象 ──────────────────────────────

    private static void SegmentRoundTrip()
    {
        T.CheckEq("CipherSizeFor(0)=84", 84L, RpuProtocol.SegmentObject.CipherSizeFor(0));
        T.CheckEq("CipherSizeFor(1)=85", 85L, RpuProtocol.SegmentObject.CipherSizeFor(1));
        T.CheckEq("CipherSizeFor(1MiB)", 68L + 1048576 + 16, RpuProtocol.SegmentObject.CipherSizeFor(1048576));
        T.CheckEq("CipherSizeFor(1MiB+1)", 68L + 1048577 + 32, RpuProtocol.SegmentObject.CipherSizeFor(1048577));
        T.CheckEq("SegmentCountFor(0)=1", 1, RpuProtocol.SegmentObject.SegmentCountFor(0));
        T.CheckEq("SegmentCountFor(1MiB)=1", 1, RpuProtocol.SegmentObject.SegmentCountFor(1048576));
        T.CheckEq("SegmentCountFor(1MiB+1)=2", 2, RpuProtocol.SegmentObject.SegmentCountFor(1048577));

        foreach (var (size, segs) in new[] { (0, 1), (1, 1), (1048576, 1), (1048576 + 12345, 2), (5 * 1048576 + 7, 6) })
        {
            var (entry, cipher, plain) = MakeObject(size % 100 + 20, size);
            using var ms = new MemoryStream(cipher);
            var decrypted = RpuProtocol.SegmentObject.DecryptAll(ms, TestObjKey);
            T.Check($"解密往返逐字节一致（{size}B/{segs}段）", decrypted.SequenceEqual(plain));
            ms.Position = 0;
            T.Check($"流式明文摘要一致（{size}B）",
                RpuProtocol.SegmentObject.PlaintextSha256(ms, TestObjKey) == entry.PlainSha256);
            ms.Position = 0;
            var header = RpuProtocol.SegmentObject.ReadHeader(ms);
            T.Check($"头解析一致（{size}B）",
                header.ObjectId == entry.ObjectId && header.PlainSize == size
                && header.SegmentCount == segs && header.NoncePrefix.SequenceEqual(TestNonce));
        }

        // 空对象（0B 明文 → 84B 密文）明文摘要是空串 SHA-256。
        var (emptyEntry, emptyCipher, _) = MakeObject(7, 0);
        T.CheckEq("空对象密文 84B", 84, emptyCipher.Length);
        using var ems = new MemoryStream(emptyCipher);
        T.CheckEq("空对象明文摘要 = SHA256(\"\")",
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            RpuProtocol.SegmentObject.PlaintextSha256(ems, TestObjKey));
    }

    private static void SegmentTamper()
    {
        var (entry, cipher, _) = MakeObject(31, 4096);

        var badCipher = (byte[])cipher.Clone();
        badCipher[100] ^= 0xFF;
        T.Check("密文字节被翻 → 段认证失败",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(badCipher), TestObjKey),
                "资源段认证失败"));

        var badTag = (byte[])cipher.Clone();
        badTag[^1] ^= 0xFF;
        T.Check("GCM tag 被翻 → 段认证失败",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(badTag), TestObjKey),
                "资源段认证失败"));

        var badMagic = (byte[])cipher.Clone();
        badMagic[0] = (byte)'X';
        T.Check("魔数被改 → 对象损坏",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(badMagic), TestObjKey),
                "加密资源已损坏，无法读取"));

        var badVersion = (byte[])cipher.Clone();
        badVersion[11] = 2;
        T.Check("版本升到 2 → 不受支持",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(badVersion), TestObjKey),
                "加密资源版本不受支持"));

        var badSegCount = (byte[])cipher.Clone();
        badSegCount[59] = 2;
        T.Check("段数说谎 → 对象损坏",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(badSegCount), TestObjKey),
                "加密资源已损坏，无法读取"));

        var truncated = new byte[cipher.Length - 1];
        Array.Copy(cipher, truncated, truncated.Length);
        T.Check("尾部截断 1B → 被截断",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(truncated), TestObjKey),
                "加密资源被截断"));

        T.Check("错误密钥 → 段认证失败",
            ThrowsRpu(() => RpuProtocol.SegmentObject.DecryptAll(new MemoryStream(cipher),
                Enumerable.Repeat((byte)0xE1, 32).ToArray()), "资源段认证失败"));
    }

    // ────────────────────────────── 清单校验 ──────────────────────────────

    private static void ManifestValidation()
    {
        var (e1, _, _) = MakeObject(41, 3000);
        var (e2, _, _) = MakeObject(42, 3000);

        RpuProtocol.ValidateManifest(TestManifest("rel1", e1, e2));
        T.Check("合法清单通过校验", true);

        var pub = RpuEd25519.PublicKeyFromSeed(TestSeed);
        var envJson = Seal(TestManifest("rel1", e1, e2), TestSeed);
        var env = JsonSerializer.Deserialize<RpuEnvelope>(envJson)!;
        var parsed = RpuProtocol.VerifyAndParseManifest(env, "rel1", pub);
        T.CheckEq("验签解析 releaseId", "rel1", parsed.ReleaseId);
        T.CheckEq("验签解析文件数", 2, parsed.Packs[0].Files.Count);

        T.Check("releaseId 不匹配 → 版本或发布标识不匹配",
            ThrowsRpu(() => RpuProtocol.VerifyAndParseManifest(env, "rel2", pub), "发布清单版本或发布标识不匹配"));
        var badSig = new RpuEnvelope(env.Payload, Convert.ToBase64String(new byte[64]));
        T.Check("坏签名 → 签名或内容无效",
            ThrowsRpu(() => RpuProtocol.VerifyAndParseManifest(badSig, "rel1", pub), "发布清单签名或内容无效"));
        T.Check("坏 payload → 签名或内容无效",
            ThrowsRpu(() => RpuProtocol.VerifyAndParseManifest(new RpuEnvelope("!!!", env.Signature), "rel1", pub),
                "发布清单签名或内容无效"));
        bool emptyThrew = false;
        try { RpuProtocol.ValidateManifest(new RpuManifest(1, "r", Array.Empty<RpuPack>())); }
        catch (RpuException) { emptyThrew = true; }
        T.Check("空清单合法（Java 无非空要求）", !emptyThrew);

        // resourcePath：与 Java Protocol.resourcePath 逐条一致
        T.Check("常规路径 OK", RpuProtocol.ResourcePathOk("mods/a.json"));
        T.Check("空路径拒绝", !RpuProtocol.ResourcePathOk(""));
        T.Check("null 路径拒绝", !RpuProtocol.ResourcePathOk(null));
        T.Check(".. 穿越段拒绝", !RpuProtocol.ResourcePathOk("a/../b"));
        T.Check("头段 . 拒绝", !RpuProtocol.ResourcePathOk("./a"));
        T.Check("中段 . 拒绝", !RpuProtocol.ResourcePathOk("a/./b"));
        T.Check("空段拒绝", !RpuProtocol.ResourcePathOk("a//b"));
        T.Check("开头 '/' 拒绝", !RpuProtocol.ResourcePathOk("/a"));
        T.Check("结尾 '/' 拒绝", !RpuProtocol.ResourcePathOk("a/"));
        T.Check("反斜杠拒绝", !RpuProtocol.ResourcePathOk(@"a\b"));
        T.Check("盘符前缀拒绝", !RpuProtocol.ResourcePathOk("C:/x"));
        T.Check("控制字符拒绝", !RpuProtocol.ResourcePathOk("a\nb"));
        T.Check("DEL 拒绝", !RpuProtocol.ResourcePathOk("a\u007fb"));
        T.Check("大写接受（线上真实 TW_spectial.png）", RpuProtocol.ResourcePathOk("assets/jsblock/pids/TW_spectial.png"));
        T.Check("__MACOSX 清单层放行（挂载期另行过滤）", RpuProtocol.ResourcePathOk("__MACOSX/a"));
        T.Check("中文字符接受（Java 无字符集限制）", RpuProtocol.ResourcePathOk("assets/列車/屏显.png"));
        T.Check("超长路径接受（Java 无长度上限）", RpuProtocol.ResourcePathOk(new string('a', 1025)));

        T.Check("同包路径重复 → 资源路径、大小或哈希无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1, e1)),
                "资源路径、大小或哈希无效"));
        var crossPack = e2 with { Path = e1.Path };
        T.Check("跨包同路径允许（Java 按包去重）", NotThrows(
            () => RpuProtocol.ValidateManifest(new RpuManifest(1, "r", new[]
            {
                new RpuPack("p1", "n", "d", null, new[] { e1 }),
                new RpuPack("p2", "n", "d", null, new[] { crossPack }),
            }))));
        T.Check("包 id 重复 → 资源包元数据无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(new RpuManifest(1, "r", new[]
            {
                new RpuPack("p1", "n", "d", null, new[] { e1 }),
                new RpuPack("p1", "n2", "d", null, new[] { e2 }),
            })), "资源包元数据无效"));
        T.Check("包 name 缺失 → 资源包元数据无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(new RpuManifest(1, "r", new[]
            {
                new RpuPack("p1", null!, "d", null, new[] { e1 }),
            })), "资源包元数据无效"));
        T.Check("包 description 缺失 → 资源包元数据无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(new RpuManifest(1, "r", new[]
            {
                new RpuPack("p1", "n", null, null, new[] { e1 }),
            })), "资源包元数据无效"));
        var cipherConflict = e1 with { Path = "other/p.json", CipherSha256 = e2.CipherSha256 };
        T.Check("同 objectId 载荷不一致 → 对象引用冲突",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1, cipherConflict)), "对象引用冲突"));
        var sizeMismatch = e1 with { Path = "other/p.json", PlainSize = 4000,
            CipherSize = RpuProtocol.SegmentObject.CipherSizeFor(4000) };
        T.Check("同 objectId 尺寸漂移 → 对象引用冲突",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1, sizeMismatch)), "对象引用冲突"));
        T.Check("尺寸与公式不符 → 资源路径、大小或哈希无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1 with { CipherSize = 999 })),
                "资源路径、大小或哈希无效"));
        T.Check("坏对象 id → 资源路径、大小或哈希无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1 with { ObjectId = "xyz" })),
                "资源路径、大小或哈希无效"));
        T.Check("坏路径 → 资源路径、大小或哈希无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1 with { Path = "../evil" })),
                "资源路径、大小或哈希无效"));
        T.Check("坏密文哈希 → 资源路径、大小或哈希无效",
            ThrowsRpu(() => RpuProtocol.ValidateManifest(TestManifest("r", e1 with { CipherSha256 = "XYZ" })),
                "资源路径、大小或哈希无效"));

        T.CheckEq("快照 releaseId 提取", "rel1", RpuProtocol.TryEnvelopeReleaseId(envJson));
        T.Check("坏 JSON 快照 → null", RpuProtocol.TryEnvelopeReleaseId("{oops") is null);
        T.Check("payload 非字符串快照 → null", RpuProtocol.TryEnvelopeReleaseId("{\"payload\":1}") is null);
    }

    // ────────────────────────────── 缓存 ──────────────────────────────

    private static void CacheLockCommit()
    {
        var gameDir = Fx.TempDir();
        var c1 = new RpuCache(gameDir);
        c1.AcquireUpdateLock();
        T.Check("首实例取锁成功", true);
        T.Check("同实例重复取锁 → 异常",
            ThrowsRpu(() => c1.AcquireUpdateLock(), "另一个客户端正在更新同一缓存"));
        var c2 = new RpuCache(gameDir);
        T.Check("第二实例跨句柄取锁 → 异常",
            ThrowsRpu(() => c2.AcquireUpdateLock(), "另一个客户端正在更新同一缓存"));
        c2.Dispose();
        c1.ReleaseLock();
        var c3 = new RpuCache(gameDir);
        try
        {
            c3.AcquireUpdateLock();
            T.Check("释放后重新取锁成功", true);
        }
        finally
        {
            c3.ReleaseLock();
            c3.Dispose();
        }

        const string envelopeRaw = "{\"payload\":\"abc\",\"signature\":\"def\"}";
        var cache = new RpuCache(Fx.TempDir());
        try
        {
            cache.CommitCurrent(envelopeRaw);
            T.CheckEq("current.json 原文一致", envelopeRaw, File.ReadAllText(cache.CurrentJsonPath));
            T.Check("current.json.tmp 已消失", !File.Exists(cache.CurrentJsonPath + ".tmp"));
            cache.CommitCurrent("{\"payload\":\"2\",\"signature\":\"3\"}");
            T.CheckEq("覆盖写后读到新值", "{\"payload\":\"2\",\"signature\":\"3\"}", File.ReadAllText(cache.CurrentJsonPath));

            var (e1, _, _) = MakeObject(51, 10);
            cache.CommitCurrent(Seal(TestManifest("rel-9", e1), TestSeed));
            T.CheckEq("快照 releaseId 解析", "rel-9", cache.ReadCurrentReleaseId());

            var fresh = new RpuCache(Fx.TempDir());
            fresh.Dispose();
            T.Check("缺失快照 → null", new RpuCache(Fx.TempDir()).ReadCurrentReleaseId() is null);
        }
        finally
        {
            cache.Dispose();
        }
    }

    private static void CacheObjectsCleanup()
    {
        var gameDir = Fx.TempDir();
        var cache = new RpuCache(gameDir);
        try
        {
            var (entry, cipher, _) = MakeObject(61, 1000);
            T.Check("缺失对象 → 无效", !cache.IsObjectValid(entry));
            T.Check("非哈希名 → 无效密文哈希", ThrowsRpu(() => cache.ObjectPath("zzz"), "无效密文哈希"));

            File.WriteAllBytes(cache.ObjectPath(entry.CipherSha256), cipher);
            T.Check("落盘后 → 有效", cache.IsObjectValid(entry));
            T.Check("尺寸不符 → 无效", !cache.IsObjectValid(entry with { CipherSize = cipher.Length + 1 }));

            var (_, otherCipher, _) = MakeObject(62, 1000);
            File.WriteAllBytes(cache.ObjectPath(entry.CipherSha256), otherCipher);
            T.Check("内容哈希不符 → 无效", !cache.IsObjectValid(entry));
            File.WriteAllBytes(cache.ObjectPath(entry.CipherSha256), cipher);

            // complete：空 part 校验失败并删除
            cache.OpenPartWrite(entry.CipherSha256).Dispose();
            T.Check("空 part → 下载文件校验失败",
                ThrowsRpu(() => cache.CompleteDownload(entry), "下载文件校验失败"));
            T.Check("失败后 part 已删除", !File.Exists(cache.PartPath(entry.CipherSha256)));

            File.WriteAllBytes(cache.PartPath(entry.CipherSha256), cipher);
            cache.CompleteDownload(entry);
            T.Check("完整 part 提升为 obj 且 part 消失",
                File.Exists(cache.ObjectPath(entry.CipherSha256)) && !File.Exists(cache.PartPath(entry.CipherSha256)));
            T.Check("提升后对象有效", cache.IsObjectValid(entry));

            // 清理：只删"未引用 + 陈旧 + 命名合规"的文件
            var oldName = Sha256Hex(Encoding.UTF8.GetBytes("stale-old")) + ".obj";
            var freshName = Sha256Hex(Encoding.UTF8.GetBytes("stale-fresh")) + ".part";
            File.WriteAllBytes(Path.Combine(cache.ObjectsDir, oldName), [1]);
            File.WriteAllBytes(Path.Combine(cache.ObjectsDir, freshName), [2]);
            File.WriteAllBytes(Path.Combine(cache.ObjectsDir, "junk.txt"), [3]);
            foreach (var name in new[] { oldName, freshName, "junk.txt" })
                File.SetLastWriteTimeUtc(Path.Combine(cache.ObjectsDir, name),
                    DateTimeOffset.UtcNow.AddDays(-9).UtcDateTime);

            var retained = new HashSet<string>(StringComparer.Ordinal) { entry.CipherSha256 };
            T.CheckEq("清理删除 2 个陈旧未引用", 2, cache.Cleanup(retained));
            T.Check("被引用对象保留", File.Exists(cache.ObjectPath(entry.CipherSha256)));
            T.Check("命名不符的文件不动", File.Exists(Path.Combine(cache.ObjectsDir, "junk.txt")));
            T.CheckEq("retentionDays=0 → 不清理", 0, cache.Cleanup(retained, 0));
            var rangeGuard = false;
            try { cache.Cleanup(retained, 6); }
            catch (ArgumentException) { rangeGuard = true; }
            T.Check("retentionDays=6 → 参数异常", rangeGuard);
        }
        finally
        {
            cache.Dispose();
        }
    }

    private static void DeploymentConfig()
    {
        var key = RpuDeploymentConfig.DecodeSigningKey(QlConstants.DefaultRpuSigningPublicKey);
        T.CheckEq("部署公钥解码 32B", 32, key.Length);
        var der = Convert.FromBase64String(QlConstants.DefaultRpuSigningPublicKey);
        T.Check("SPKI 头部是 Ed25519 标准前缀",
            der.AsSpan(0, 12).SequenceEqual(new byte[] { 0x30, 0x2a, 0x30, 0x05, 0x06, 0x03, 0x2b, 0x65, 0x70, 0x03, 0x21, 0x00 }));
        T.Check("解码 = DER 末 32 字节", key.AsSpan().SequenceEqual(der.AsSpan(12)));
        T.Check("坏 base64 → RpuException",
            ThrowsRpu(() => RpuDeploymentConfig.DecodeSigningKey("!!"), "签名公钥无效"));
        T.Check("过短 DER → RpuException",
            ThrowsRpu(() => RpuDeploymentConfig.DecodeSigningKey(Convert.ToBase64String(new byte[8])),
                "签名公钥无效"));
    }

    // ────────────────────────────── 客户端 ──────────────────────────────

    private static void NormalizeCases()
    {
        T.CheckEq("https 正常保留", "https://rpu.example.com/api/v1/client",
            RpuClient.NormalizeApiBase("https://rpu.example.com/api/v1/client"));
        T.CheckEq("尾斜杠修剪", "https://rpu.example.com/api/v1/client",
            RpuClient.NormalizeApiBase("https://rpu.example.com/api/v1/client/"));
        T.Check("缺后缀 → 异常",
            ThrowsRpu(() => RpuClient.NormalizeApiBase("https://rpu.example.com/api"), "更新服务地址无效"));
        T.Check("http 非回环 → 异常",
            ThrowsRpu(() => RpuClient.NormalizeApiBase("http://rpu.example.com/api/v1/client"), "仅允许本机调试"));
        T.CheckEq("http 回环放行", "http://127.0.0.1:9000/api/v1/client",
            RpuClient.NormalizeApiBase("http://127.0.0.1:9000/api/v1/client/"));
        T.CheckEq("localhost 放行", "http://localhost:9000/api/v1/client",
            RpuClient.NormalizeApiBase("http://localhost:9000/api/v1/client"));
        T.Check("ftp scheme → 异常",
            ThrowsRpu(() => RpuClient.NormalizeApiBase("ftp://x/api/v1/client"), "更新服务地址无效"));
    }

    private static async Task ClientShape()
    {
        NormalizeCases();
        var server = await MiniHttpServer.StartAsync(@"%TEMP%\ql-tmp\rpu-fake").ConfigureAwait(false);
        try
        {
            var sessionExp = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
            server.AddJsonSequence("/api/v1/client/sessions",
                "{\"protocolVersion\":1,\"token\":\"TOK-1\",\"expiresAt\":\"" + sessionExp + "\"}");
            server.AddResponder("/api/v1/client/releases/latest", (_, _) => (200,
                "{\"protocolVersion\":1,\"releaseId\":\"rel-shape\"}"u8.ToArray()));

            using var client = new RpuClient(server.BaseUrl + "api/v1/client");
            var latest = await client.GetLatestAsync().ConfigureAwait(false);
            T.CheckEq("latest releaseId", "rel-shape", latest.ReleaseId);
            await client.GetLatestAsync().ConfigureAwait(false);
            T.CheckEq("会话复用（sessions 仅签发 1 次）", 1, server.Hits("/api/v1/client/sessions"));
            T.Check("会话请求形状（protocolVersion/modVersion/minecraftVersion）",
                server.Bodies("/api/v1/client/sessions")[0].Contains("\"protocolVersion\":1")
                && server.Bodies("/api/v1/client/sessions")[0].Contains("\"modVersion\":\"1.0.0\"")
                && server.Bodies("/api/v1/client/sessions")[0].Contains("\"minecraftVersion\":\"1.20.1\""));
            T.Check("RPU UA 请求头",
                server.Headers("/api/v1/client/releases/latest")[0].Contains("User-Agent: QingLanRPU/1.0 protocol/1"));
            T.Check("Bearer 会话令牌",
                server.Headers("/api/v1/client/releases/latest")[0].Contains("Authorization: Bearer TOK-1"));
            T.Check("Cache-Control no-store",
                server.Headers("/api/v1/client/releases/latest")[0].Contains("Cache-Control: no-store"));

            // 429 → 退避重试一次成功（Retry-After: 0 → 默认 1s）
            var ticketExp = DateTimeOffset.UtcNow.AddMinutes(10).ToString("o");
            server.AddResponder("/api/v1/client/download-tickets", (call, _) => call == 1
                ? (429, Array.Empty<byte>())
                : (200, Encoding.UTF8.GetBytes(
                    "{\"tickets\":[{\"objectId\":\"" + ObjectIdOf(71) + "\",\"url\":\"" + server.BaseUrl +
                    "objects/x.obj\",\"expiresAt\":\"" + ticketExp + "\"}]}")));
            var tickets = await client.GetTicketsAsync("rel-shape", "src-1", new[] { ObjectIdOf(71) })
                .ConfigureAwait(false);
            T.CheckEq("429 退避后重试成功", 2, server.Hits("/api/v1/client/download-tickets"));
            T.CheckEq("工单解析 1 张", 1, tickets.Count);
            T.Check("工单在有效期", RpuClient.TicketUsable(tickets[0]));
            T.Check("过期工单不可用", !RpuClient.TicketUsable(
                new RpuTicket("x", "https://a/b", DateTimeOffset.UtcNow.AddSeconds(-9).ToString("o"))));
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task ClientErrors()
    {
        var server = await MiniHttpServer.StartAsync(@"%TEMP%\ql-tmp\rpu-fake2").ConfigureAwait(false);
        try
        {
            var sessionExp = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
            server.AddJsonSequence("/api/v1/client/sessions",
                "{\"protocolVersion\":1,\"token\":\"TOK-E\",\"expiresAt\":\"" + sessionExp + "\"}");
            using var client = new RpuClient(server.BaseUrl + "api/v1/client");

            server.AddResponder("/api/v1/client/releases/latest", (_, _) => (500, "oops"u8.ToArray()));
            await CheckRpuAsync("500 → 服务暂时不可用", "更新服务暂时不可用",
                () => client.GetLatestAsync()).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/latest", (_, _) => (401, Array.Empty<byte>()));
            await CheckRpuAsync("401（重签后仍 401）→ 在线授权失败", "在线授权失败",
                () => client.GetLatestAsync()).ConfigureAwait(false);
            T.CheckEq("401 路径触发强制重签", 2, server.Hits("/api/v1/client/sessions"));

            server.AddResponder("/api/v1/client/releases/latest", (_, _) => (200, "{oops"u8.ToArray()));
            await CheckRpuAsync("坏 JSON → 响应格式无效", "更新服务响应格式无效",
                () => client.GetLatestAsync()).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/latest",
                (_, _) => (200, Encoding.UTF8.GetBytes("{\"protocolVersion\":2,\"releaseId\":\"r\"}")));
            await CheckRpuAsync("协议版本 2 → 不兼容", "客户端协议版本不兼容",
                () => client.GetLatestAsync()).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/latest",
                (_, _) => (200, new byte[5 * 1024 * 1024]));
            await CheckRpuAsync("5MB 响应 → 超过允许大小", "服务端响应超过允许大小",
                () => client.GetLatestAsync()).ConfigureAwait(false);

            await CheckRpuAsync("manifest 404 → 发布或接口不存在", "发布或接口不存在",
                () => client.GetEnvelopeAsync("rel-404")).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/rel-env/manifest", (_, _) => (200, "{}"u8.ToArray()));
            await CheckRpuAsync("信封缺字段 → 响应格式无效", "更新服务响应格式无效",
                () => client.GetEnvelopeAsync("rel-env")).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/sources", (_, _) =>
                (200, "{\"sources\":[]}"u8.ToArray()));
            await CheckRpuAsync("空来源 → 暂无可用下载源", "暂无可用下载源",
                () => client.GetSourcesAsync("rel-s")).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/sources", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"sources\":[{\"id\":\"dup\",\"name\":\"a\",\"available\":true,\"probeUrl\":null}," +
                "{\"id\":\"dup\",\"name\":\"b\",\"available\":true,\"probeUrl\":null}]}")));
            await CheckRpuAsync("来源重复 id → 响应格式无效", "更新服务响应格式无效",
                () => client.GetSourcesAsync("rel-s")).ConfigureAwait(false);

            T.Check("129 个工单 → 批次非法",
                ThrowsRpu(() => client.GetTicketsAsync("rel", "src", Enumerable.Repeat("x", 129).ToList()).GetAwaiter().GetResult(),
                    "内部错误：下载批次大小非法"));

            var ticketExp = DateTimeOffset.UtcNow.AddMinutes(10).ToString("o");
            server.AddResponder("/api/v1/client/download-tickets", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"tickets\":[{\"objectId\":\"" + ObjectIdOf(81) + "\",\"url\":\"" + server.BaseUrl +
                "objects/a.obj\",\"expiresAt\":\"" + ticketExp + "\"}]}")));
            await CheckRpuAsync("工单数量不符 → 响应格式无效", "更新服务响应格式无效",
                () => client.GetTicketsAsync("rel", "src-1", new[] { ObjectIdOf(81), ObjectIdOf(82) }))
                .ConfigureAwait(false);

            server.AddResponder("/api/v1/client/download-tickets", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"tickets\":[{\"objectId\":\"" + ObjectIdOf(81) + "\",\"url\":\"" + server.BaseUrl +
                "objects/a.obj\",\"expiresAt\":\"2020-01-01T00:00:00Z\"}]}")));
            await CheckRpuAsync("过期工单 → 响应格式无效", "更新服务响应格式无效",
                () => client.GetTicketsAsync("rel", "src-1", new[] { ObjectIdOf(81) })).ConfigureAwait(false);

            var key32 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
            var key31 = Convert.ToBase64String(Enumerable.Repeat((byte)7, 31).ToArray());
            server.AddResponder("/api/v1/client/releases/relk/keys", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"keys\":{\"" + ObjectIdOf(83) + "\":\"" + key32 + "\"}}")));
            await CheckRpuAsync("keys 缺 id → 响应格式无效", "更新服务响应格式无效",
                () => client.GetKeysAsync("relk", new[] { ObjectIdOf(83), ObjectIdOf(84) })).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/relk/keys", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"keys\":{\"" + ObjectIdOf(83) + "\":\"" + key31 + "\"}}")));
            await CheckRpuAsync("keys 31B → 响应格式无效", "更新服务响应格式无效",
                () => client.GetKeysAsync("relk", new[] { ObjectIdOf(83) })).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/relk/keys", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"keys\":{\"" + ObjectIdOf(85) + "\":\"" + key32 + "\"}}")));
            await CheckRpuAsync("keys 多返回未请求的 id → 响应格式无效", "更新服务响应格式无效",
                () => client.GetKeysAsync("relk", new[] { ObjectIdOf(83) })).ConfigureAwait(false);

            server.AddResponder("/api/v1/client/releases/relk2/keys", (_, _) => (200, Encoding.UTF8.GetBytes(
                "{\"keys\":{\"" + ObjectIdOf(83) + "\":\"" + key32 + "\"}}")));
            var good = await client.GetKeysAsync("relk2", new[] { ObjectIdOf(83) }).ConfigureAwait(false);
            T.Check("keys 正常返回 32B 密钥", good[ObjectIdOf(83)].Length == 32);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ────────────────────────────── 引擎端到端 ──────────────────────────────

    private static async Task EngineEndToEnd()
    {
        var gameDir = Fx.TempDir();
        var server = await MiniHttpServer.StartAsync(@"%TEMP%\ql-tmp\rpu-e2e").ConfigureAwait(false);
        try
        {
            var ticketExp = DateTimeOffset.UtcNow.AddMinutes(10).ToString("o");
            var sessionExp = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
            var apiBase = server.BaseUrl + "api/v1/client";
            var pubKey = RpuEd25519.PublicKeyFromSeed(TestSeed);

            server.AddJsonSequence("/api/v1/client/sessions",
                "{\"protocolVersion\":1,\"token\":\"TOK-ENGINE\",\"expiresAt\":\"" + sessionExp + "\"}");
            server.AddFile("/api/v1/client/releases/latest",
                Encoding.UTF8.GetBytes("{\"protocolVersion\":1,\"releaseId\":\"rel-e2e\"}"));

            var (e1, c1, _) = MakeObject(91, 2000);
            var (e2, c2, _) = MakeObject(92, 1048576 + 12345);
            var envelopeJson = Seal(TestManifest("rel-e2e", e1, e2), TestSeed);
            server.AddFile("/api/v1/client/releases/rel-e2e/manifest", Encoding.UTF8.GetBytes(envelopeJson));

            server.AddResponder("/api/v1/client/releases/rel-e2e/check", (_, body) =>
            {
                var doc = JsonDocument.Parse(body);
                var required = new List<string>();
                foreach (var o in doc.RootElement.GetProperty("objects").EnumerateArray())
                    if (!o.GetProperty("valid").GetBoolean())
                        required.Add(o.GetProperty("objectId").GetString()!);
                return (200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { requiredObjectIds = required })));
            });

            server.AddFile("/probe.bin", "probe-ok"u8.ToArray());
            server.AddFile("/api/v1/client/sources", Encoding.UTF8.GetBytes(
                "{\"sources\":[{\"id\":\"src-1\",\"name\":\"测试源\",\"priority\":0,\"available\":true,\"probeUrl\":\"" +
                server.BaseUrl + "probe.bin\"}]}"));

            server.AddResponder("/api/v1/client/download-tickets", (_, body) =>
            {
                var doc = JsonDocument.Parse(body);
                var ids = doc.RootElement.GetProperty("objectIds").EnumerateArray()
                    .Select(x => x.GetString()!).ToList();
                var tickets = ids.Select(id => new
                {
                    objectId = id,
                    url = server.BaseUrl + "objects/" + id + ".obj",
                    expiresAt = ticketExp,
                });
                return (200, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tickets })));
            });
            server.AddFile("/objects/" + e1.ObjectId + ".obj", c1);
            server.AddFile("/objects/" + e2.ObjectId + ".obj", c2);

            var stages = new List<RpuSyncStage>();
            RpuSyncResult result;
            using (var engine = new RpuSyncEngine(gameDir, (apiBase, pubKey)))
            {
                engine.ProgressChanged += p => { lock (stages) stages.Add(p.Stage); };
                result = await engine.SyncAsync().ConfigureAwait(false);
            }

            T.CheckEq("run1 releaseId", "rel-e2e", result.ReleaseId);
            T.CheckEq("run1 全量下载 2", 2, result.Downloaded);
            T.CheckEq("run1 本地复用 0", 0, result.ValidFiles);
            T.Check("run1 有下载任务且未取消", !result.NothingToDownload && !result.Canceled);
            var distinct = stages.Distinct().ToList();
            T.Check("六阶段齐全且有序", distinct.SequenceEqual(new[]
            {
                RpuSyncStage.Initializing, RpuSyncStage.Authorizing, RpuSyncStage.Checking,
                RpuSyncStage.Sources, RpuSyncStage.Downloading, RpuSyncStage.Ready,
            }));

            using (var cache = new RpuCache(gameDir))
            {
                T.Check("对象按密文哈希落盘且逐字节一致",
                    File.ReadAllBytes(cache.ObjectPath(e1.CipherSha256)).SequenceEqual(c1)
                    && File.ReadAllBytes(cache.ObjectPath(e2.CipherSha256)).SequenceEqual(c2));
                T.CheckEq("current.json 字节一致", envelopeJson, File.ReadAllText(cache.CurrentJsonPath));
                T.Check("current.json.tmp 已清理", !File.Exists(cache.CurrentJsonPath + ".tmp"));
                T.Check("快照 releaseId 一致", cache.ReadCurrentReleaseId() == "rel-e2e");
            }
            T.CheckEq("全程未调用 /keys", 0, server.Hits("/api/v1/client/releases/rel-e2e/keys"));
            T.Check("check 请求带 valid/cipherSha256 形状",
                server.Bodies("/api/v1/client/releases/rel-e2e/check")[0].Contains("\"valid\":false")
                && server.Bodies("/api/v1/client/releases/rel-e2e/check")[0].Contains("\"cipherSha256\":\"\""));
            T.Check("工单请求带 releaseId/sourceId",
                server.Bodies("/api/v1/client/download-tickets")[0].Contains("\"releaseId\":\"rel-e2e\"")
                && server.Bodies("/api/v1/client/download-tickets")[0].Contains("\"sourceId\":\"src-1\""));
            T.Check("探测源真实访问了 probeUrl", server.Hits("/probe.bin") == 1);
            var manifestHitsAfterRun1 = server.Hits("/api/v1/client/releases/rel-e2e/manifest");

            // run2：全部有效 → 无需下载、不拉清单、不探测源
            using (var engine2 = new RpuSyncEngine(gameDir, (apiBase, pubKey)))
            {
                var result2 = await engine2.SyncAsync().ConfigureAwait(false);
                T.Check("run2 无需下载", result2.NothingToDownload && result2.Downloaded == 0);
                T.CheckEq("run2 本地复用 2", 2, result2.ValidFiles);
                T.CheckEq("run2 本地快照复用（manifest 不再在线拉）", manifestHitsAfterRun1,
                    server.Hits("/api/v1/client/releases/rel-e2e/manifest"));
                T.CheckEq("run2 不探测源", 1, server.Hits("/probe.bin"));
                T.Check("run2 check 上报 valid=true 与密文哈希",
                    server.Bodies("/api/v1/client/releases/rel-e2e/check").Any(b =>
                        b.Contains("\"valid\":true") && b.Contains(e1.CipherSha256)));
            }

            // run3：陈旧未引用对象按保留期清理
            var stalePath = Path.Combine(gameDir, "qlrpu", "cache", "objects",
                Sha256Hex(Encoding.UTF8.GetBytes("stale-e2e")) + ".obj");
            Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
            File.WriteAllBytes(stalePath, [9]);
            File.SetLastWriteTimeUtc(stalePath, DateTimeOffset.UtcNow.AddDays(-9).UtcDateTime);
            using (var engine3 = new RpuSyncEngine(gameDir, (apiBase, pubKey)))
            {
                var result3 = await engine3.SyncAsync().ConfigureAwait(false);
                T.CheckEq("run3 清理 1 个陈旧对象", 1, result3.CleanedStale);
                T.Check("run3 陈旧对象已删除", !File.Exists(stalePath));
            }

            // run4：本地对象被篡改 → 重新下载自愈
            var target = Path.Combine(gameDir, "qlrpu", "cache", "objects", e1.CipherSha256 + ".obj");
            var corrupt = File.ReadAllBytes(target);
            corrupt[70] ^= 0xFF;
            File.WriteAllBytes(target, corrupt);
            using (var engine4 = new RpuSyncEngine(gameDir, (apiBase, pubKey)))
            {
                var result4 = await engine4.SyncAsync().ConfigureAwait(false);
                T.CheckEq("run4 检出篡改并重下 1 个", 1, result4.Downloaded);
                T.Check("run4 内容自愈", File.ReadAllBytes(target).SequenceEqual(c1));
            }

            T.CheckEq("四次运行四次签发会话", 4, server.Hits("/api/v1/client/sessions"));
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
