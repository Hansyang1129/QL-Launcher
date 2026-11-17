using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Qinglan.Launcher.Core.Sync.Rpu;

/// <summary>
/// RPU 资源缓存（<c>&lt;gameDir&gt;/qlrpu/cache</c>）：加密对象仓库 + 发布快照 + 跨进程更新锁。
/// 布局与 qlrpu-client 的 CacheStore 逐项对齐：<c>objects/&lt;密文sha256&gt;.obj</c> 为通过密文
/// 校验的对象、同前缀 <c>.part</c> 为下载中的临时文件、<c>current.json</c> 为最近一次认证通过的
/// 发布信封、<c>update.lock</c> 为更新期互斥锁（与 Java FileLock 同为区域锁，可互查）。
/// 启动器只做缓存预热：永不解密落盘、永不触碰 objects 目录之外的任何文件。
/// </summary>
public sealed class RpuCache : IDisposable
{
    /// <summary>未引用对象的保留天数（Java 端默认 7 天；合法区间 7..90，0 表示不清理）。</summary>
    public const int RetentionDays = 7;

    private static readonly Regex FileNamePattern = new("^([0-9a-f]{64})\\.(obj|part)$", RegexOptions.Compiled);
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>缓存根目录。</summary>
    public string Root { get; }
    /// <summary>加密对象目录。</summary>
    public string ObjectsDir { get; }
    /// <summary>发布快照文件。</summary>
    public string CurrentJsonPath { get; }
    /// <summary>跨进程更新锁文件。</summary>
    public string LockPath { get; }

    private FileStream? _lockStream;

    public RpuCache(string gameDir)
    {
        Root = Path.Combine(gameDir, "qlrpu", "cache");
        ObjectsDir = Path.Combine(Root, "objects");
        CurrentJsonPath = Path.Combine(Root, "current.json");
        LockPath = Path.Combine(Root, "update.lock");
        Directory.CreateDirectory(ObjectsDir);
    }

    // ────────────────────────────── 更新锁 ──────────────────────────────

    /// <summary>
    /// 获取跨进程更新锁（非阻塞尝试，与 Java FileChannel.tryLock 语义一致）。
    /// 同一 JVM/进程重复获取、或锁被其他客户端（含游戏内的 Mod）持有时抛 RpuException。
    /// </summary>
    public void AcquireUpdateLock()
    {
        if (_lockStream is not null)
            throw new RpuException("另一个客户端正在更新同一缓存，请稍后重试。");
        FileStream fs;
        try
        {
            fs = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (IOException e)
        {
            Globals.Log("打开 update.lock 失败：" + e.Message);
            throw new RpuException("另一个客户端正在更新同一缓存，请稍后重试。");
        }
        try
        {
            fs.Lock(0, 1);
        }
        catch (IOException)
        {
            fs.Dispose();
            throw new RpuException("另一个客户端正在更新同一缓存，请稍后重试。");
        }
        _lockStream = fs;
    }

    /// <summary>释放更新锁（幂等）。</summary>
    public void ReleaseLock()
    {
        var fs = _lockStream;
        _lockStream = null;
        if (fs is null) return;
        try { fs.Unlock(0, 1); } catch { /* 句柄关闭时锁自动释放 */ }
        fs.Dispose();
    }

    /// <inheritdoc/>
    public void Dispose() => ReleaseLock();

    // ────────────────────────────── 发布快照 ──────────────────────────────

    /// <summary>读取 current.json 原文（信封 JSON）；不存在或暂时不可读为 <see langword="null"/>。</summary>
    public string? ReadCurrentEnvelopeRaw()
    {
        try
        {
            return File.Exists(CurrentJsonPath) ? File.ReadAllText(CurrentJsonPath, Utf8NoBom) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Globals.Log("读取 current.json 失败（视为缺失）：" + e.Message);
            return null;
        }
    }

    /// <summary>本地快照记录的发布标识（宽松解析，不验签）；缺失或损坏为 <see langword="null"/>。</summary>
    public string? ReadCurrentReleaseId()
    {
        var raw = ReadCurrentEnvelopeRaw();
        return raw is null ? null : RpuProtocol.TryEnvelopeReleaseId(raw);
    }

    /// <summary>提交发布快照：写 current.json.tmp → 落盘 → 原子替换 current.json。</summary>
    public void CommitCurrent(string rawEnvelopeJson)
    {
        var tmp = CurrentJsonPath + ".tmp";
        File.WriteAllText(tmp, rawEnvelopeJson, Utf8NoBom);
        try
        {
            using var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.Flush(flushToDisk: true);
        }
        catch (IOException e)
        {
            Globals.Log("快照落盘刷写失败（继续替换）：" + e.Message);
        }
        AtomicMove(tmp, CurrentJsonPath);
    }

    // ────────────────────────────── 加密对象 ──────────────────────────────

    /// <summary>对象落盘路径：objects/&lt;密文sha256&gt;.obj（文件名即校验和，自证完整）。</summary>
    public string ObjectPath(string cipherSha256)
    {
        if (!RpuProtocol.HashOk(cipherSha256))
            throw new RpuException("无效密文哈希");
        return Path.Combine(ObjectsDir, cipherSha256 + ".obj");
    }

    /// <summary>下载临时文件路径：objects/&lt;密文sha256&gt;.part。</summary>
    public string PartPath(string cipherSha256)
    {
        if (!RpuProtocol.HashOk(cipherSha256))
            throw new RpuException("无效密文哈希");
        return Path.Combine(ObjectsDir, cipherSha256 + ".part");
    }

    /// <summary>
    /// 本地对象是否完好：存在常规文件、尺寸等于 cipherSize、且整个文件的 SHA-256 等于其文件名
    /// （cipherSha256）。与 CacheStore.valid 语义一致；文件被占用等读不到的情况一律视为无效，
    /// 由服务端 check 决定重新下载。
    /// </summary>
    public bool IsObjectValid(RpuFileEntry entry)
    {
        try
        {
            using var fs = new FileStream(ObjectPath(entry.CipherSha256), FileMode.Open,
                FileAccess.Read, FileShare.Read | FileShare.Write | FileShare.Delete);
            if (fs.Length != entry.CipherSize) return false;
            return Sha256Hex(fs).Equals(entry.CipherSha256, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>打开对象的 .part 临时文件供写入（覆盖旧内容）。</summary>
    public FileStream OpenPartWrite(string cipherSha256)
        => new(PartPath(cipherSha256), FileMode.Create, FileAccess.Write, FileShare.None, 256 * 1024);

    /// <summary>
    /// 下载完成落定（CacheStore.complete 语义）：校验 .part 尺寸与密文哈希，通过则刷盘后原子
    /// 替换为 .obj；不通过则删除临时文件并抛出（由上层按重试策略重新下载）。
    /// </summary>
    public void CompleteDownload(RpuFileEntry entry)
    {
        var part = PartPath(entry.CipherSha256);
        long size;
        string hash;
        try
        {
            using var fs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read);
            size = fs.Length;
            hash = Sha256Hex(fs);
        }
        catch (IOException e)
        {
            throw new RpuException("下载文件校验失败，已清除损坏临时文件。", e);
        }
        if (size != entry.CipherSize || !hash.Equals(entry.CipherSha256, StringComparison.Ordinal))
        {
            try { File.Delete(part); } catch { /* 删不掉也照抛，下次会覆盖重建 */ }
            throw new RpuException("下载文件校验失败，已清除损坏临时文件。");
        }
        try
        {
            using var fs = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            fs.Flush(flushToDisk: true);
        }
        catch (IOException e)
        {
            Globals.Log("对象落盘刷写失败（继续提交）：" + e.Message);
        }
        AtomicMove(part, ObjectPath(entry.CipherSha256));
    }

    /// <summary>
    /// 清理缓存内不再被当前发布引用的陈旧对象（CacheStore.cleanup 语义）：
    /// 只处理 objects 目录下命名严格匹配 <c>[0-9a-f]{64}.(obj|part)</c> 的文件，
    /// 未被引用且最后写入早于保留期才删除；其余文件一律不碰。返回删除数量。
    /// </summary>
    public int Cleanup(IReadOnlySet<string> retained, int retentionDays = RetentionDays)
    {
        if (retentionDays == 0) return 0;
        if (retentionDays < 7 || retentionDays > 90)
            throw new ArgumentException("Invalid cache retention", nameof(retentionDays));
        if (!Directory.Exists(ObjectsDir)) return 0;

        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(retentionDays);
        int removed = 0;
        foreach (var path in Directory.EnumerateFiles(ObjectsDir))
        {
            var m = FileNamePattern.Match(Path.GetFileName(path));
            if (!m.Success || retained.Contains(m.Groups[1].Value)) continue;
            try
            {
                if (File.GetLastWriteTimeUtc(path) >= cutoff) continue;
                File.Delete(path);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 被占用（游戏正在读）就留给下次。
            }
        }
        return removed;
    }

    // ────────────────────────────── 内部工具 ──────────────────────────────

    /// <summary>原子替换：同目录改名覆盖；失败短暂重试一次后按磁盘不支持原子替换抛出。</summary>
    internal static void AtomicMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (IOException e)
        {
            Thread.Sleep(100);
            try
            {
                File.Move(from, to, overwrite: true);
            }
            catch
            {
                Globals.Log("原子替换失败 " + from + " -> " + to + "：" + e.Message);
                throw new RpuException("缓存磁盘不支持原子替换，请将游戏移至本地 NTFS 等支持的磁盘。");
            }
        }
    }

    /// <summary>流式 SHA-256 十六进制小写摘要。</summary>
    internal static string Sha256Hex(Stream stream)
    {
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[256 * 1024];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            hasher.AppendData(buffer, 0, n);
        return Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
    }
}
