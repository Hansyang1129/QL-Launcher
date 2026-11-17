using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qinglan.Launcher.Core.Sync.Rpu;

/// <summary>RPU 协议层业务异常：Message 为可直接展示给用户的简中文案。</summary>
public class RpuException : Exception
{
    public RpuException(string message) : base(message) { }
    public RpuException(string message, Exception inner) : base(message, inner) { }
}

// ────────────────────────────── 协议模型 ──────────────────────────────

public sealed record RpuSession(int ProtocolVersion, string Token, string ExpiresAt);

public sealed record RpuLatest(int ProtocolVersion, string ReleaseId);

/// <summary>发布清单信封：payload 为清单 JSON 的 base64，signature 为其 Ed25519 签名的 base64。</summary>
public sealed record RpuEnvelope(
    [property: JsonPropertyName("payload")] string Payload,
    [property: JsonPropertyName("signature")] string Signature);

public sealed record RpuFileEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("objectId")] string ObjectId,
    [property: JsonPropertyName("plainSize")] long PlainSize,
    [property: JsonPropertyName("cipherSize")] long CipherSize,
    [property: JsonPropertyName("plainSha256")] string PlainSha256,
    [property: JsonPropertyName("cipherSha256")] string CipherSha256);

public sealed record RpuPack(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("packFormat"), JsonConverter(typeof(RpuNumberOrStringConverter))] string? PackFormat,
    [property: JsonPropertyName("files")] IReadOnlyList<RpuFileEntry> Files);

public sealed record RpuManifest(
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion,
    [property: JsonPropertyName("releaseId")] string ReleaseId,
    [property: JsonPropertyName("packs")] IReadOnlyList<RpuPack> Packs);

public sealed record RpuSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("priority")] int Priority,
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("probeUrl")] string? ProbeUrl);

public sealed record RpuTicket(
    [property: JsonPropertyName("objectId")] string ObjectId,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt);

/// <summary>下载对象密钥集（objectId → 32B AES 密钥的 base64）。</summary>
public sealed record RpuKeySet(IReadOnlyDictionary<string, string> Keys);

/// <summary>
/// 数字或字符串二象兼容的 string 属性转换器（packFormat 用）：
/// 服务端是 Gson 序列化，同一字段在不同发布里可能是数字（15）也可能是字符串（"15"）。
/// 数字按不变文化转十进制文本；null 原样通过；其余形态按 JsonException 处理。
/// </summary>
public sealed class RpuNumberOrStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l)
                ? l.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null or JsonTokenType.None => null,
            _ => throw new JsonException(),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

// ────────────────────────────── 校验 ──────────────────────────────

public static partial class RpuProtocol
{
    /// <summary>协议版本（服务端与客户端共同遵守）。</summary>
    public const int Version = 1;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,80}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    /// <summary>发布/来源/工单标识：1..80 位 [A-Za-z0-9_-]。</summary>
    public static bool IdOk(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>SHA-256 十六进制小写摘要。</summary>
    public static bool HashOk(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    /// <summary>
    /// 宽松解析信封 JSON（本地 current.json 快照用，不做验签），提取 payload 内清单的 releaseId。
    /// 任何解析失败都返回 <see langword="null"/>，由调用方回退在线获取。
    /// </summary>
    public static string? TryEnvelopeReleaseId(string envelopeJson)
    {
        try
        {
            using var envelope = JsonDocument.Parse(envelopeJson);
            var payload = envelope.RootElement.GetProperty("payload").GetString();
            if (string.IsNullOrEmpty(payload)) return null;
            using var manifest = JsonSerializer.Deserialize<JsonDocument>(Convert.FromBase64String(payload));
            return manifest is not null
                && manifest.RootElement.ValueKind == JsonValueKind.Object
                && manifest.RootElement.TryGetProperty("releaseId", out var rid)
                && rid.ValueKind == JsonValueKind.String ? rid.GetString() : null;
        }
        catch (Exception)
        {
            // 宽松解析：本地快照任何形态问题都视为缺失，由调用方回退在线获取。
            return null;
        }
    }

    /// <summary>
    /// 资源路径合法性（与 Java Protocol.resourcePath 逐条一致）：非空、不以 / 开头或结尾、
    /// 不含反斜杠、无 Windows 盘符前缀、每段非空且非 "."/".."、不含 &lt;0x20 控制字符与 0x7F。
    /// 不限制字符集与长度——大小写、__MACOSX 等由挂载期索引另行过滤，清单校验不管。
    /// </summary>
    public static bool ResourcePathOk(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path[0] == '/' || path[^1] == '/') return false;
        if (path.IndexOf('\\') >= 0) return false;
        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0])) return false;
        foreach (var seg in path.Split('/'))
            if (seg.Length == 0 || seg == "." || seg == "..")
                return false;
        foreach (var ch in path)
            if (ch < ' ' || ch == '\x7f')
                return false;
        return true;
    }

    /// <summary>信封验签 + 清单解析 + 结构校验。失败抛 RpuException（文案与 Java 端一致）。</summary>
    /// <param name="signingKeyRaw32">部署公钥的 32 字节裸钥（SPKI 去头）。</param>
    public static RpuManifest VerifyAndParseManifest(RpuEnvelope envelope, string expectedReleaseId,
        ReadOnlySpan<byte> signingKeyRaw32)
    {
        byte[] payload;
        try { payload = Convert.FromBase64String(envelope.Payload); }
        catch (FormatException) { throw new RpuException("发布清单签名或内容无效，请联系管理员。"); }
        byte[] signature;
        try { signature = Convert.FromBase64String(envelope.Signature); }
        catch (FormatException) { throw new RpuException("发布清单签名或内容无效，请联系管理员。"); }
        if (!RpuEd25519.Verify(signingKeyRaw32, payload, signature))
            throw new RpuException("发布清单签名或内容无效，请联系管理员。");

        RpuManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<RpuManifest>(payload);
        }
        catch (JsonException)
        {
            throw new RpuException("发布清单签名或内容无效，请联系管理员。");
        }
        // Java validate 的前置合并检查：manifest/版本/releaseId/packs 任一不符都是这一句。
        if (manifest is null || manifest.Packs is null)
            throw new RpuException("发布清单版本或发布标识不匹配。");
        if (manifest.ProtocolVersion != Version || manifest.ReleaseId != expectedReleaseId)
            throw new RpuException("发布清单版本或发布标识不匹配。");
        ValidateManifest(manifest);
        return manifest;
    }

    /// <summary>
    /// 清单结构校验（与 Java Protocol.validate 一致）：包元数据（id/name/description/files 齐全、
    /// id 全局唯一）、条目字段合法（失败统一报"资源路径、大小或哈希无效。"）、包内路径唯一、
    /// objectId 载荷四字段一致、cipherSha256 归属唯一。空 packs 合法（Java 无非空要求）。
    /// </summary>
    public static void ValidateManifest(RpuManifest manifest)
    {
        var packIds = new HashSet<string>(StringComparer.Ordinal);
        var objectEntries = new Dictionary<string, RpuFileEntry>(StringComparer.Ordinal);
        var cipherOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pack in manifest.Packs)
        {
            if (!IdOk(pack.Id) || !packIds.Add(pack.Id)
                || pack.Name is null || pack.Description is null || pack.Files is null)
                throw new RpuException("资源包元数据无效。");
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in pack.Files)
            {
                if (!ResourcePathOk(f.Path) || !paths.Add(f.Path)
                    || !HashOk(f.ObjectId) || !HashOk(f.CipherSha256) || !HashOk(f.PlainSha256)
                    || f.PlainSize < 0 || f.CipherSize != SegmentObject.CipherSizeFor(f.PlainSize))
                    throw new RpuException("资源路径、大小或哈希无效。");
                if (objectEntries.TryGetValue(f.ObjectId, out var known))
                {
                    if (known.CipherSha256 != f.CipherSha256 || known.CipherSize != f.CipherSize
                        || known.PlainSha256 != f.PlainSha256 || known.PlainSize != f.PlainSize)
                        throw new RpuException("对象引用冲突。");
                }
                else
                {
                    objectEntries[f.ObjectId] = f;
                }
                if (cipherOwners.TryGetValue(f.CipherSha256, out var owner) && owner != f.ObjectId)
                    throw new RpuException("密文对象冲突。");
                cipherOwners[f.CipherSha256] = f.ObjectId;
            }
        }
    }

    // ────────────────────────────── 分段加密对象 ──────────────────────────────

    /// <summary>QRPUOBJ1 分段加密对象容器（与 Java 端 SegmentedObject 二进制一致）。</summary>
    public static class SegmentObject
    {
        public const int SegmentSize = 1048576;
        public const int HeaderSize = 68;
        private static readonly byte[] Magic = "QRPUOBJ1"u8.ToArray();

        public static long SegmentCountFor(long plainSize)
            => Math.Max(1, (plainSize + SegmentSize - 1) / SegmentSize);

        /// <summary>密文总长 = 68 头 + 明文长 + 16×段数（GCM 128 位 tag）。</summary>
        public static long CipherSizeFor(long plainSize)
            => HeaderSize + plainSize + 16L * SegmentCountFor(plainSize);

        public sealed record Header(string ObjectId, long PlainSize, int SegmentCount, byte[] NoncePrefix);

        /// <summary>读取并校验 68 字节对象头。</summary>
        public static Header ReadHeader(Stream s)
        {
            var header = new byte[HeaderSize];
            ReadExactly(s, header);
            if (!header.AsSpan(0, 8).SequenceEqual(Magic))
                throw new RpuException("加密资源已损坏，无法读取。");
            int version = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(8, 4));
            if (version != 1)
                throw new RpuException("加密资源版本不受支持，请更新客户端。");
            var objectId = Convert.ToHexString(header.AsSpan(12, 32)).ToLowerInvariant();
            long plainSize = BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(44, 8));
            int segSize = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(52, 4));
            if (segSize != SegmentSize)
                throw new RpuException("加密资源已损坏，无法读取。");
            int segCount = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(56, 4));
            if (segCount < 1 || segCount != SegmentCountFor(plainSize))
                throw new RpuException("加密资源已损坏，无法读取。");
            if (s.CanSeek && s.Length - HeaderSize != plainSize + 16L * segCount)
                throw new RpuException("加密资源被截断。");
            var noncePrefix = new byte[8];
            Buffer.BlockCopy(header, 60, noncePrefix, 0, 8);
            return new Header(objectId, plainSize, segCount, noncePrefix);
        }

        /// <summary>整对象解密（小对象）；失败抛 RpuException。</summary>
        public static byte[] DecryptAll(Stream s, ReadOnlySpan<byte> key32)
        {
            var header = ReadHeader(s);
            var plain = new byte[header.PlainSize];
            DecryptSegments(s, header, key32, (segIndex, buf, len) => Buffer.BlockCopy(buf, 0, plain, segIndex * SegmentSize, len));
            return plain;
        }

        /// <summary>流式解密并计算明文 SHA-256（大对象不落盘、不整载内存）。</summary>
        public static string PlaintextSha256(Stream s, ReadOnlySpan<byte> key32)
        {
            var header = ReadHeader(s);
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            DecryptSegments(s, header, key32, (_, buf, len) => hasher.AppendData(buf, 0, len));
            return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        }

        private static void DecryptSegments(Stream s, Header header, ReadOnlySpan<byte> key32,
            Action<int, byte[], int> sink)
        {
            using var aes = new AesGcm(key32.ToArray(), 16);
            var headerBytes = new byte[HeaderSize];
            BuildHeaderBytes(header, headerBytes);
            var aad = new byte[HeaderSize + 8];
            Buffer.BlockCopy(headerBytes, 0, aad, 0, HeaderSize);
            var iv = new byte[12];
            Buffer.BlockCopy(header.NoncePrefix, 0, iv, 0, 8);
            var buffer = new byte[SegmentSize + 16];
            for (int seg = 0; seg < header.SegmentCount; seg++)
            {
                int plainLen = (int)Math.Min(SegmentSize, header.PlainSize - (long)seg * SegmentSize);
                ReadExactly(s, buffer.AsSpan(0, plainLen + 16));
                BinaryPrimitives.WriteInt32BigEndian(iv.AsSpan(8, 4), seg);
                BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(HeaderSize, 4), seg);
                BinaryPrimitives.WriteInt32BigEndian(aad.AsSpan(HeaderSize + 4, 4), plainLen);
                try
                {
                    // 签名序：Decrypt(nonce, ciphertext, tag, plaintext, associatedData)；密文原地解密。
                    aes.Decrypt(iv, buffer.AsSpan(0, plainLen), buffer.AsSpan(plainLen, 16),
                        buffer.AsSpan(0, plainLen), aad);
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw new RpuException("资源段认证失败，已拒绝读取。");
                }
                sink(seg, buffer, plainLen);
            }
        }

        /// <summary>重建 68 字节头（IV/AAD 需要完整头作关联数据）。</summary>
        private static void BuildHeaderBytes(Header h, byte[] dst)
        {
            Magic.CopyTo(dst, 0);
            BinaryPrimitives.WriteInt32BigEndian(dst.AsSpan(8, 4), 1);
            var idBytes = Convert.FromHexString(h.ObjectId);
            idBytes.CopyTo(dst, 12);
            BinaryPrimitives.WriteInt64BigEndian(dst.AsSpan(44, 8), h.PlainSize);
            BinaryPrimitives.WriteInt32BigEndian(dst.AsSpan(52, 4), SegmentSize);
            BinaryPrimitives.WriteInt32BigEndian(dst.AsSpan(56, 4), h.SegmentCount);
            h.NoncePrefix.CopyTo(dst, 60);
        }
    }

    internal static void ReadExactly(Stream s, Span<byte> target)
    {
        int total = 0;
        while (total < target.Length)
        {
            int n = s.Read(target[total..]);
            if (n <= 0)
                throw new EndOfStreamException();
            total += n;
        }
    }
}
