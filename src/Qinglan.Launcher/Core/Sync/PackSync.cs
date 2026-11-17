using System.IO.Compression;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core;

/// <summary>一次包应用的结果总账。</summary>
/// <param name="Downloaded">从下载或 overrides 载荷写入的文件数</param>
/// <param name="Unchanged">已与包 SHA-1 一致的文件数</param>
/// <param name="Renamed">收养 plain/disabled 姊妹件而就位的文件数</param>
/// <param name="Deleted">包不再列出而被移除的文件数</param>
/// <param name="HandedOver">因归游戏内 RPU 客户端所有而跳过删除的旧追踪文件数</param>
/// <param name="Failed">写不动的文件数</param>
/// <param name="FailedPaths">失败文件的逻辑路径（供 UI 展示）</param>
public sealed record QlPackOutcome(int Downloaded, int Unchanged, int Renamed, int Deleted,
    int HandedOver, int Failed, IReadOnlyList<string> FailedPaths);

/// <summary>轻蓝更新引擎，由整合包自己的 .mrpack 驱动（QLPackSync 同规）。</summary>
/// <para>更新路径就是包文件本身：本地或远端，<c>modrinth.index.json</c> 加内嵌
/// <c>overrides/</c> 载荷描述了实例应有的一切。引擎按 SHA-1 与实例对账后才动手：
/// 内容一致的文件零成本，陈旧的替换，包不再列出的清除，结果全部记进
/// <see cref="QlSyncState"/> 供下一轮续跑。</para>
/// <para>归属：resourcepacks 与光影、配置、mod 一样归 mrpack 同步管；
/// 游戏内 RPU 客户端独占的 <c>qlrpu/</c> 与 <c>config/qlrpu-client.json</c>
/// 在这里绝不写入或删除。</para>
public static class QlPackSync
{
    /// <summary>实例内缓存整合包文件的目录（同步页在用户没挑包时回落到这里）。</summary>
    public const string CacheDir = QlConstants.PackCacheDir;

    /// <summary>返回包索引里记录的 versionId，如 <c>26.9.1</c>（已剥 -full 后缀）。</summary>
    /// <exception cref="IOException">包读不开或缺 index 时。</exception>
    public static string ReadPackVersion(string mrpackPath)
        => QlMrpackManifest.BuildFromMrpack(mrpackPath).PackVersionOrDefault();

    /// <summary>包提供的可选 mod 组，按首次出现顺序。</summary>
    /// <exception cref="IOException">包读不开时。</exception>
    public static IReadOnlyList<string> OptionalGroups(string mrpackPath)
    {
        var manifest = QlMrpackManifest.BuildFromMrpack(mrpackPath);
        var groups = new List<string>();
        foreach (var entry in manifest.ChannelOrThrow(QlManifest.ChannelMods).Mods)
            if (entry.Optional != null && !groups.Contains(entry.Optional))
                groups.Add(entry.Optional);
        return groups;
    }

    /// <summary>把一份整合包应用到一个实例：写、换、删，使实例匹配包，且绝不碰 RPU 客户端所有物。</summary>
    /// <param name="gameDir">实例的运行目录</param>
    /// <param name="mrpackPath">包文件（本地路径）</param>
    /// <param name="packUrl">为后续运行记录的更新源；<see langword="null"/> 保留已记录值，
    /// 空白值按 <see langword="null"/> 存</param>
    /// <param name="listener">可选进度回调 (finished, total)</param>
    /// <param name="ct">下载用的协作取消令牌（与 HMCL 行为无关，UI 目前不取消）</param>
    /// <exception cref="IOException">包读不开或状态存不进时。</exception>
    public static QlPackOutcome Sync(string gameDir, string mrpackPath, string? packUrl = null,
        Action<int, int>? listener = null, CancellationToken ct = default)
    {
        var manifest = QlMrpackManifest.BuildFromMrpack(mrpackPath);

        QlSyncState existing;
        try
        {
            existing = QlSyncState.Load(gameDir);
        }
        catch (IOException e)
        {
            Globals.Log("轻蓝状态读不动，按空状态起步: " + e.Message);
            existing = QlSyncState.Empty();
        }

        // 全新实例没有可选记录：按包随附状态整组应用。记录一旦存在，
        // 缺席的组就代表用户主动禁用。
        var respectsOptionalRecord = existing.OptionalRecorded || existing.EnabledOptional.Count > 0;
        var enabledGroups = new List<string>();
        var allGroups = new List<string>();

        var modsFolder = Path.Combine(gameDir, QlDirectoryLayout.ActiveModsFolder);
        var planned = new List<PlannedFile>();
        foreach (var entry in manifest.ChannelOrThrow(QlManifest.ChannelMods).Mods)
        {
            var logical = entry.Path ?? throw new ManifestException("manifest path missing");
            var raw = logical.Substring(logical.IndexOf('/', 1) + 1);
            var enabled = true;
            if (entry.Optional != null)
            {
                if (!allGroups.Contains(entry.Optional)) allGroups.Add(entry.Optional);
                enabled = !respectsOptionalRecord || existing.EnabledOptional.Contains(entry.Optional);
                if (enabled && !enabledGroups.Contains(entry.Optional)) enabledGroups.Add(entry.Optional);
            }
            planned.Add(new PlannedFile(QlManifest.ChannelMods, logical, entry,
                Path.Combine(modsFolder, enabled ? raw : raw + QlConstants.DisabledSuffix)));
        }
        foreach (var entry in manifest.ChannelOrThrow(QlManifest.ChannelCommon).Files)
        {
            var logical = entry.Path ?? throw new ManifestException("manifest path missing");
            if (QlManifest.IsRpuOwned(logical)) continue;
            planned.Add(new PlannedFile(QlManifest.ChannelCommon, logical, entry,
                Path.Combine(gameDir, logical.Replace('/', Path.DirectorySeparatorChar))));
        }

        var tracked = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var plannedPaths = new HashSet<string>(StringComparer.Ordinal);
        var failedSet = new HashSet<string>(StringComparer.Ordinal);
        var failedPaths = new List<string>();
        int downloaded = 0, unchanged = 0, renamed = 0, deleted = 0, handedOver = 0, failed = 0;
        int total = planned.Count + CountTracked(existing);
        int finished = 0;

        using (var zip = ZipFile.OpenRead(mrpackPath))
        {
            foreach (var file in planned)
            {
                plannedPaths.Add(file.LogicalPath);
                var expected = file.Entry.Sha1 ?? throw new ManifestException("manifest sha1 missing");
                try
                {
                    if (Matches(file.Target, expected))
                    {
                        unchanged++;
                    }
                    else if (AdoptSibling(file.Target, expected))
                    {
                        renamed++;
                    }
                    else if (WriteFile(zip, file, expected, ct))
                    {
                        downloaded++;
                    }
                    else
                    {
                        failed++;
                        failedPaths.Add(file.LogicalPath);
                        failedSet.Add(file.LogicalPath);
                    }
                }
                catch (IOException e)
                {
                    failed++;
                    failedPaths.Add(file.LogicalPath);
                    failedSet.Add(file.LogicalPath);
                    Globals.Log("应用失败 " + file.LogicalPath + ": " + e.Message);
                }
                if (!failedSet.Contains(file.LogicalPath))
                {
                    if (!tracked.TryGetValue(file.Channel, out var map))
                        tracked[file.Channel] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                    map[file.LogicalPath] = expected;
                }
                finished++;
                NotifyProgress(listener, finished, total);
            }
        }

        // 清除包不再列出的文件。RPU 独占的遗留只做交接，不删。
        var previous = existing.Tracked.ToDictionary(
            kv => kv.Key, kv => new List<string>(kv.Value.Keys), StringComparer.Ordinal);
        foreach (var (channel, paths) in previous)
        {
            foreach (var logical in paths)
            {
                if (plannedPaths.Contains(logical)) continue;
                if (QlManifest.IsRpuOwned(logical))
                {
                    handedOver++;
                }
                else if (DeleteManagedFile(gameDir, modsFolder, logical))
                {
                    deleted++;
                }
                finished++;
                NotifyProgress(listener, finished, total);
            }
        }

        existing.ActiveChannel = QlManifest.ChannelMods;
        existing.ReplaceTracked(tracked);
        // Java 同款 fallback（QLPackSync 同行）：停用全部可选组时 enabledGroups 为空，
        // 记录回落随包状态——下一轮按启用计划把 .disabled 姊妹收养还原。刻意保持一致，
        // 让两边启动器对同一实例目录产生同样的状态记录。
        existing.EnabledOptional = new List<string>(enabledGroups.Count > 0 ? enabledGroups : allGroups);
        existing.OptionalRecorded = true;
        existing.PackVersion = manifest.PackVersionOrDefault();
        if (packUrl != null)
            existing.PackUrl = packUrl.Trim().Length == 0 ? null : packUrl.Trim();
        existing.Save(gameDir);

        try
        {
            QlDblPack.Scan(gameDir, manifest, QlManifest.ChannelMods, existing.EnabledOptionalGroups())
                .Save(gameDir);
        }
        catch (Exception e)
        {
            // 与 Java 同宽（IOException | RuntimeException）：清单生成绝不拖累同步本身。
            Globals.Log("dblpack 清单重生成失败(忽略): " + e.Message);
        }

        return new QlPackOutcome(downloaded, unchanged, renamed, deleted, handedOver, failed, failedPaths);
    }

    // ───────────────────────── 对账与写入 ─────────────────────────

    private sealed record PlannedFile(string Channel, string LogicalPath, QlManifest.FileEntry Entry, string Target);

    private static int CountTracked(QlSyncState state)
        => state.Tracked.Values.Sum(map => map.Count);

    private static void NotifyProgress(Action<int, int>? listener, int finished, int total)
    {
        if (listener != null && total > 0) listener(finished, total);
    }

    /// <summary>文件存在且 SHA-1 等于期望值。</summary>
    private static bool Matches(string file, string expectedSha1)
    {
        if (!File.Exists(file)) return false;
        try
        {
            return QlHttp.Sha1Hex(file).Equals(expectedSha1, StringComparison.Ordinal);
        }
        catch (IOException e)
        {
            Globals.Log("哈希失败: " + file + " -> " + e.Message);
            return false;
        }
    }

    /// <summary>目标文件的 plain/disabled 姊妹件已带正确内容时收养（改名就位，免下载）。</summary>
    private static bool AdoptSibling(string target, string expectedSha1)
    {
        var sibling = InvertedSibling(target);
        if (!Matches(sibling, expectedSha1)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        File.Move(sibling, target, overwrite: true);
        return true;
    }

    /// <summary>从 overrides 载荷或下载 URL 写一个文件；写入后按包哈希校验。</summary>
    private static bool WriteFile(ZipArchive zip, PlannedFile file, string expectedSha1, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file.Target) ?? ".");
        var payload = ReadOverride(zip, file.LogicalPath);
        if (payload != null)
        {
            WriteAtomically(file.Target, payload);
            return Verify(file.Target, expectedSha1);
        }
        foreach (var url in file.Entry.Urls)
        {
            if (!QlHttp.IsValidHttpUrl(url)) continue;
            try
            {
                DownloadTo(url, file.Target, expectedSha1, ct);
                if (Verify(file.Target, expectedSha1)) return true;
                Globals.Log("下载内容与包哈希不符: " + url);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 用户取消：整个同步退出，换 URL 无意义
            }
            catch (Exception e) when (e is IOException or HttpRequestException or TaskCanceledException)
            {
                // 该候选失败（网络/超时/内容不符）：换下一个 URL，与 Java 的 IOException 分支同义。
                Globals.Log("下载失败 " + url + ": " + e.Message);
            }
        }
        return false;
    }

    /// <summary>读 overrides 载荷里的一个条目；包没有内嵌这个文件时为 null。</summary>
    private static byte[]? ReadOverride(ZipArchive zip, string logicalPath)
    {
        var entry = zip.GetEntry(logicalPath) ?? zip.GetEntry("overrides/" + logicalPath);
        if (entry == null || entry.FullName.EndsWith('/')) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>刚落盘的文件对照包哈希检查，错了就删。</summary>
    private static bool Verify(string file, string expectedSha1)
    {
        if (Matches(file, expectedSha1)) return true;
        try { File.Delete(file); } catch (IOException) { /* 删不掉留待下轮 */ }
        return false;
    }

    /// <summary>把一个 URL 下载进目标（走启动器网络栈；候选顺序与 HMCL 一致）。</summary>
    private static void DownloadTo(string url, string target, string expectedSha1, CancellationToken ct)
        => QlHttp.DownloadToFileAsync(new[] { url }, target, expectedSha1, ct)
            .GetAwaiter().GetResult();

    /// <summary>经姊妹临时文件写盘，途中崩溃绝不会留下半个文件。</summary>
    private static void WriteAtomically(string target, byte[] content)
    {
        var temp = Path.Combine(Path.GetDirectoryName(target) ?? ".",
            Path.GetFileName(target) + ".qlimport");
        File.WriteAllBytes(temp, content);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>删除一个本启动器装过的文件，plain/disabled 两种形态都试。</summary>
    private static bool DeleteManagedFile(string gameDir, string modsFolder, string logicalPath)
    {
        string target;
        if (logicalPath.StartsWith("mods/", StringComparison.Ordinal))
        {
            target = Path.Combine(modsFolder, logicalPath["mods/".Length..]);
        }
        else
        {
            target = Path.Combine(gameDir, logicalPath.Replace('/', Path.DirectorySeparatorChar));
        }
        var removed = DeleteIfExists(target);
        var sibling = InvertedSibling(target);
        return DeleteIfExists(sibling) || removed;
    }

    private static bool DeleteIfExists(string path)
    {
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>同目录下与目标互为 plain ↔ disabled 的姊妹路径。</summary>
    private static string InvertedSibling(string target)
    {
        var name = Path.GetFileName(target);
        var dir = Path.GetDirectoryName(target) ?? ".";
        var inverted = name.EndsWith(QlConstants.DisabledSuffix, StringComparison.Ordinal)
            ? name[..^QlConstants.DisabledSuffix.Length]
            : name + QlConstants.DisabledSuffix;
        return Path.Combine(dir, inverted);
    }
}
