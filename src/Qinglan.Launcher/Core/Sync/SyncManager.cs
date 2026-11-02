using System.IO;
using System.Security.Cryptography;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core;

/// <summary>对账结果。</summary>
public enum QlReconcile { Ok, Rename, Download }

/// <summary>一个 manifest 条目解析到的落盘位置。</summary>
public sealed record QlPlannedFile(string Channel, string LogicalPath, QlManifest.FileEntry Entry, string PhysicalPath)
{
    /// <summary>展示标签：<c>std/Foo.jar</c>；common 频道不带频道前缀。</summary>
    public string Label()
        => (QlManifest.ModsChannels.Contains(Channel) ? Channel + "/" : "") + Path.GetFileName(PhysicalPath);
}

/// <summary>一次同步的总账。Java 端语义逐一保留：RENAME 不计入 unchanged。</summary>
public sealed class QlSyncOutcome
{
    public int Downloaded { get; internal set; }
    public int Unchanged { get; internal set; }
    public int Deleted { get; internal set; }
    public int Renamed { get; internal set; }
    public HashSet<string> FailedFiles { get; } = new(StringComparer.Ordinal);
    public bool Canceled { get; internal set; }
    public bool ChannelSwitched { get; internal set; }
    public string? PackVersion { get; internal set; }
}

/// <summary>“查更新”用的干跑分析（不写盘、不下载）。</summary>
public sealed class QlSyncAnalysis
{
    public string FromChannel { get; internal set; } = "std";
    public string ToChannel { get; internal set; } = "std";
    public bool ChannelSwitchPending { get; internal set; }
    public int Unchanged { get; internal set; }
    public List<QlPlannedFile> NeedDownload { get; } = new();
    public int RenamesPending { get; internal set; }
    public int DeletesPending { get; internal set; }
    public List<string> EnabledOptional { get; } = new();
    public bool IsIdle => NeedDownload.Count == 0 && RenamesPending == 0 && DeletesPending == 0
                          && !ChannelSwitchPending;
}

/// <summary>把实例增量更新到一份 manifest（可重启、可分析、只动 tracked 文件）。</summary>
public static class QlSyncManager
{
    /// <summary>下载并行度（HMCL 走自己的执行池；这里用有界并发即可）。</summary>
    private const int DownloadParallelism = 8;

    // ────────────────────────────── 入口 ──────────────────────────────

    /// <summary>执行完整同步。进度：对账（hash）阶段每个条目报一次(finished,total)；
    /// 下载阶段不报中间进度（与 HMCL 实现同口径）。</summary>
    public static QlSyncOutcome Sync(string gameDir, QlManifest manifest, string targetChannel,
        Dictionary<string, bool>? optionalExtras = null, Action<int, int>? listener = null,
        CancellationToken ct = default)
    {
        var state = QlSyncState.Load(gameDir);
        var outcome = new QlSyncOutcome();
        RunCore(gameDir, manifest, state, targetChannel, optionalExtras, listener, apply: true, outcome, ct);
        return outcome;
    }

    /// <summary>干跑：只算差距，不动磁盘、不发下载。</summary>
    public static QlSyncAnalysis Analyze(string gameDir, QlManifest manifest, string targetChannel,
        Dictionary<string, bool>? optionalExtras = null)
    {
        var state = QlSyncState.Load(gameDir);
        var summary = new QlSyncAnalysis();
        RunCore(gameDir, manifest, state, targetChannel, optionalExtras, null, apply: false, outcomeBundle: null,
            CancellationToken.None, summary);
        return summary;
    }

    // ────────────────────────────── 核心 ──────────────────────────────

    private static void RunCore(string gameDir, QlManifest manifest, QlSyncState state, string targetChannel,
        Dictionary<string, bool>? optionalExtras, Action<int, int>? listener, bool apply,
        QlSyncOutcome? outcomeBundle, CancellationToken ct, QlSyncAnalysis? summary = null)
    {
        // --- 1. 频道方向 ------------------------------------------------------------
        var marker = QlDirectoryLayout.DetectActiveChannel(gameDir, state.ActiveChannel);
        var fromChannel = marker ?? (QlManifest.ModsChannels.Contains(state.ActiveChannel)
            ? state.ActiveChannel : "std");
        var toChannel = QlManifest.ModsChannels.Contains(targetChannel) ? targetChannel : fromChannel;
        var switchPending = !fromChannel.Equals(toChannel, StringComparison.Ordinal);

        if (!apply)
        {
            summary!.FromChannel = fromChannel;
            summary!.ToChannel = toChannel;
            summary!.ChannelSwitchPending = switchPending;
        }

        if (apply)
        {
            if (switchPending)
            {
                QlDirectoryLayout.SwitchChannel(gameDir, fromChannel, toChannel);
                outcomeBundle!.ChannelSwitched = true;
            }
            state.ActiveChannel = toChannel;
            QlDirectoryLayout.Recover(gameDir, toChannel);
        }

        // 规划时假装已经切到目标频道（与 Java 的时序一致：先切换再规划）。
        var plannerActive = toChannel;

        // --- 2. 可选组 --------------------------------------------------------------
        if (optionalExtras is not null)
        {
            foreach (var (group, enable) in optionalExtras)
                state.SetOptionalEnabled(group, enable);
        }
        var enabledOptional = state.EnabledOptionalGroups();
        if (!apply)
        {
            foreach (var group in enabledOptional) summary!.EnabledOptional.Add(group);
        }

        // --- 3. 期望状态：全部频道条目 + common -------------------------------------
        var planned = CollectDesiredFiles(manifest, gameDir, plannerActive, enabledOptional);

        // --- 4. purge：只在 tracked 且已从 manifest 消失时 ----------------------------
        var desiredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in planned)
            desiredKeys.Add(ChannelKey(f.Channel, f.LogicalPath));

        var stale = new List<string>();
        foreach (var (channel, files) in new Dictionary<string, Dictionary<string, string>>(state.Tracked))
        foreach (var logicalPath in new List<string>(files.Keys))
        {
            if (desiredKeys.Contains(ChannelKey(channel, logicalPath))) continue;
            stale.Add(ChannelKey(channel, logicalPath));
            if (apply)
            {
                DeleteTrackedFile(gameDir, plannerActive, channel, logicalPath);
                outcomeBundle!.Deleted++;
            }
        }
        if (!apply) summary!.DeletesPending = stale.Count;

        // --- 5. 对账 ------------------------------------------------------------------
        var unchanged = new List<QlPlannedFile>();
        var downloads = new List<QlPlannedFile>();
        int renames = 0, finished = 0, total = planned.Count, renamePending = 0;
        foreach (var file in planned)
        {
            var result = Reconcile(file, apply);
            switch (result)
            {
                case QlReconcile.Ok: unchanged.Add(file); break;
                case QlReconcile.Rename:
                    if (apply) renames++; else renamePending++;
                    break;
                case QlReconcile.Download:
                    if (apply) downloads.Add(file); else summary!.NeedDownload.Add(file);
                    break;
            }
            finished++;
            listener?.Invoke(finished, total);
        }
        if (apply) outcomeBundle!.Renamed = renames;
        // 下载前先落地 unchanged：之后一旦部分失败，这一轮也照实统计。
        if (apply) outcomeBundle!.Unchanged = unchanged.Count;
        if (!apply) summary!.RenamesPending = renamePending;

        // --- 6. 下载 -------------------------------------------------------------------
        if (apply && downloads.Count > 0)
        {
            // 任何非法 URL 在规划阶段就该把事情说透（Java 同形：IOException 带文件名）。
            foreach (var f in downloads)
            foreach (var url in f.Entry.Urls)
                if (!QlHttp.IsValidHttpUrl(url))
                    throw new IOException("Invalid download URL for " + f.LogicalPath + ": " + url);

            var bag = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ok = 0;
            try
            {
                Parallel.ForEachAsync(downloads,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = DownloadParallelism,
                        CancellationToken = ct,
                    },
                    async (f, token) =>
                    {
                        var good = await QlHttp.DownloadToFileAsync(f.Entry.Urls, f.PhysicalPath,
                            f.Entry.Sha1!, token).ConfigureAwait(false);
                        if (good) Interlocked.Increment(ref ok);
                        else bag.Add(f.Label());
                    }).ConfigureAwait(false).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                foreach (var failed in bag) outcomeBundle!.FailedFiles.Add(failed);
                outcomeBundle!.Canceled = true;
                return;
            }
            outcomeBundle!.Downloaded = ok;
            foreach (var failed in bag)
            {
                if (outcomeBundle.FailedFiles.Add(failed))
                    Globals.Log("下载终失败: " + failed);
            }
            if (outcomeBundle.FailedFiles.Count > 0) return; // 部分失败：下轮 sync 再收尾
            outcomeBundle.Downloaded = downloads.Count - bag.Count;
        }

        // --- 7. 记账 ---------------------------------------------------------------------
        if (apply)
        {
            var newTracked = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
            foreach (var file in planned)
            {
                if (!newTracked.TryGetValue(file.Channel, out var map))
                    newTracked[file.Channel] = map = new Dictionary<string, string>(StringComparer.Ordinal);
                map[file.LogicalPath] = file.Entry.Sha1!;
            }
            state.ReplaceTracked(newTracked);
            state.PackVersion = manifest.PackVersionOrDefault();
            state.Save(gameDir);

            // dblpack 清单尽量跟着刷新，失败绝不拖累同步。
            try
            {
                QlDblPack.Scan(gameDir, manifest, toChannel, enabledOptional).Save(gameDir);
            }
            catch (Exception e)
            {
                Globals.Log("dblpack 刷新失败(忽略): " + e.Message);
            }
            outcomeBundle!.PackVersion = state.PackVersion;
        }
    }

    // ─────────────────────────── 期望状态 ───────────────────────────

    private static List<QlPlannedFile> CollectDesiredFiles(QlManifest manifest, string gameDir,
        string activeChannel, IReadOnlySet<string> enabledOptional)
    {
        var planned = new List<QlPlannedFile>();
        foreach (var channel in QlManifest.ModsChannels)
        {
            var modsFolder = QlDirectoryLayout.ModsDirectory(gameDir, channel, activeChannel);
            // 每一条都要规划；被禁的可选 mod 保持 *.jar.disabled 停在原地，不删。
            foreach (var entry in manifest.ChannelOrThrow(channel).Mods)
            {
                var enabled = string.IsNullOrWhiteSpace(entry.Optional) || enabledOptional.Contains(entry.Optional!);
                var raw = RawFileName(entry);
                var target = Path.Combine(modsFolder, enabled ? raw : raw + QlConstants.DisabledSuffix);
                planned.Add(new QlPlannedFile(channel, entry.Path!, entry, target));
            }
        }
        foreach (var entry in manifest.ChannelOrThrow(QlManifest.ChannelCommon).Files)
        {
            var target = Path.Combine(gameDir, entry.Path!.Replace('/', Path.DirectorySeparatorChar));
            planned.Add(new QlPlannedFile(QlManifest.ChannelCommon, entry.Path!, entry, target));
        }
        return planned;
    }

    // ─────────────────────────── 对账与清理 ───────────────────────────

    private static QlReconcile Reconcile(QlPlannedFile file, bool apply)
    {
        var target = file.PhysicalPath;
        var inverted = InvertedPath(target);
        var expectedSha = file.Entry.Sha1!;

        if (File.Exists(target) && ShaMatches(target, file.Entry.Size, expectedSha))
            return QlReconcile.Ok;

        if (File.Exists(inverted) && ShaMatches(inverted, file.Entry.Size, expectedSha))
        {
            if (apply)
                MoveFile(inverted, target);
            return QlReconcile.Rename;
        }
        return QlReconcile.Download;
    }

    private static void DeleteTrackedFile(string gameDir, string activeChannel, string channel, string logicalPath)
    {
        var folder = QlManifest.ModsChannels.Contains(channel)
            ? QlDirectoryLayout.ModsDirectory(gameDir, channel, activeChannel)
            : gameDir;
        var raw = LogicalToRaw(logicalPath, channel);
        foreach (var name in new[] { raw, raw + QlConstants.DisabledSuffix })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            try
            {
                File.Delete(path);
                Globals.Log("删除清单已回收的文件: " + path);
            }
            catch (IOException e)
            {
                Globals.Log("删除失败(已忽略): " + path + " -> " + e.Message);
            }
        }
    }

    private static string LogicalToRaw(string logicalPath, string channel)
        => QlManifest.ModsChannels.Contains(channel)
            ? logicalPath.Substring(logicalPath.IndexOf('/', 1) + 1)
            : logicalPath.Replace('/', Path.DirectorySeparatorChar);

    private static string ChannelKey(string channel, string logicalPath) => channel + "::" + logicalPath;

    private static string RawFileName(QlManifest.FileEntry entry)
    {
        var p = entry.Path ?? throw new ManifestException("manifest path missing");
        return p.Substring(p.IndexOf('/', 1) + 1);
    }

    /// <summary>尺寸预检 + SHA-1；尺寸缺失（只可能来自手写 manifest）时退化为纯摘要。</summary>
    private static bool ShaMatches(string path, long? expectedSize, string expectedSha)
    {
        try
        {
            var fi = new FileInfo(path);
            if (expectedSize is > 0 && fi.Length != expectedSize.Value) return false;
            using var fs = File.OpenRead(path);
            return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant()
                .Equals(expectedSha, StringComparison.Ordinal);
        }
        catch (IOException e)
        {
            Globals.Log("哈希失败: " + path + " -> " + e.Message);
            return false;
        }
    }

    private static string InvertedPath(string target)
    {
        var name = Path.GetFileName(target);
        var dir = Path.GetDirectoryName(target)!;
        return name.EndsWith(QlConstants.DisabledSuffix, StringComparison.Ordinal)
            ? Path.Combine(dir, name[..^QlConstants.DisabledSuffix.Length])
            : Path.Combine(dir, name + QlConstants.DisabledSuffix);
    }

    private static void MoveFile(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
        File.Move(source, target, overwrite: true);
    }
}
