using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core;

/// <summary>把 mrpack（Modrinth 格式种子包）安进游戏目录，然后做「QL 化」变身轻蓝实例。</summary>
/// <para>两步走：Install 严格安装 zip 内容（overrides + index 文件，缺的从 CDN 补大），
/// Seedize（Java 语义逐条保留）负责布局 healed + sha1 对账 tracked + 状态文件 + dblpack；
/// 内容不匹配的文件不动也不登记，等下一轮 sync 重下覆盖。</para>
public static class QlSeedImporter
{
    /// <summary>mrpack 安装摘要。</summary>
    public sealed class InstallOutcome
    {
        public int OverridesCopied;
        public int FilesDownloaded;
        public int FilesFailed;
        public string? VersionId;
        public string? Name;
        public string? Minecraft;
        public string? FabricLoader;
    }

    // ───────────────────────── mrpack 安装 ─────────────────────────

    /// <summary>把 .mrpack 内容装进 gameDir：overrides 覆盖拷贝 + index 文件按需下载。</summary>
    /// <exception cref="IOException">zip 结构或 index 无效时。</exception>
    public static async Task<InstallOutcome> InstallMrpackAsync(string mrpackPath, string gameDir,
        Action<int, int>? listener = null, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var root = ReadIndexEntry(zip);

        MrpackIndex? index;
        {
            await using var s = root.Open();
            index = await JsonSerializer.DeserializeAsync<MrpackIndex>(s, JsonOpts, ct);
        }
        if (index == null || index.FormatVersion != 1)
            throw new IOException("mrpack index 无效（formatVersion 必须 = 1）");
        if (!string.Equals(index.Game, "minecraft", StringComparison.OrdinalIgnoreCase))
            throw new IOException("mrpack target game = " + (index.Game ?? "?") + "，仅支持 minecraft");

        foreach (var dep in new[] { index.Dependencies?.Minecraft, index.Dependencies?.FabricLoader })
            if (string.IsNullOrWhiteSpace(dep))
                throw new IOException("mrpack 缺少 minecraft / fabric-loader 依赖声明");

        Directory.CreateDirectory(gameDir);
        var outcome = new InstallOutcome
        {
            VersionId = index.VersionId,
            Name = index.Name,
            Minecraft = index.Dependencies!.Minecraft,
            FabricLoader = index.Dependencies!.FabricLoader,
        };

        // 1. overrides/（含 client-overrides/）冲突拷贝覆盖；server-overrides/ 仅服务端，跳过。
        foreach (var file in zip.Entries)
        {
            var fullName = file.FullName.Replace('\\', '/');
            if (string.IsNullOrEmpty(fullName) || fullName.EndsWith('/')) continue;
            string? rel = fullName.StartsWith("overrides/") ? fullName["overrides/".Length..]
                : fullName.StartsWith("client-overrides/") ? fullName["client-overrides/".Length..]
                : null;
            if (string.IsNullOrEmpty(rel)) continue;
            if (rel.Contains(':') || rel.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains(".."))
                throw new IOException("mrpack overrides 含不安全路径: " + rel);
            var target = Path.Combine(gameDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            file.ExtractToFile(target, overwrite: true);
            outcome.OverridesCopied++;
        }

        // 2. index 文件：客户端 relevant 的就应当存在，缺/茬就拉。
        //    env.client=unsupported 的条目（仅服务端）直接跳过。 HMCL-QL 实测：FULL=LITE-CDN 逻辑逐文件。
        var relevant = index.Files.Where(f => !string.Equals(f.Env?.Client, "unsupported",
            StringComparison.OrdinalIgnoreCase)).ToList();
        int finished = 0;
        await Parallel.ForEachAsync(relevant,
            new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (f, token) =>
            {
                if (f.Downloads is not { Count: > 0 } urls)
                {
                    Interlocked.Increment(ref outcome.FilesFailed);
                    Globals.Log("mrpack 条目无下载地址: " + f.Path);
                }
                else if (await QlHttp.DownloadToFileAsync(urls, Path.Combine(gameDir, f.Path.Replace('/', Path.DirectorySeparatorChar)), f.Hashes?.Sha1 ?? "", token))
                {
                    Interlocked.Increment(ref outcome.FilesDownloaded);
                }
                else
                {
                    Interlocked.Increment(ref outcome.FilesFailed);
                    Globals.Log("mrpack 文件下载失败(同步兜底): " + f.Path);
                }
                var n = Interlocked.Increment(ref finished);
                listener?.Invoke(n, relevant.Count);
            });
        // mrpack 下载失败不阻断：QL 化后下一轮 sync 还会精准补齐。
        return outcome;
    }

    /// <summary>mrpack 头部预览（不落盘）：名字、versionId、目标游戏与依赖版本，供导入前展示。</summary>
    /// <exception cref="IOException">zip 里缺 modrinth.index.json 或 index 解析不动。</exception>
    public static MrpackHead ReadHead(string mrpackPath)
    {
        using var zip = ZipFile.OpenRead(mrpackPath);
        var entry = ReadIndexEntry(zip);
        MrpackIndex? index;
        try
        {
            using var s = entry.Open();
            index = JsonSerializer.Deserialize<MrpackIndex>(s, JsonOpts);
        }
        catch (JsonException e)
        {
            throw new IOException("mrpack index 解析失败: " + e.Message, e);
        }
        if (index is null || index.FormatVersion != 1)
            throw new IOException("mrpack index 无效（formatVersion 必须 = 1）");
        return new MrpackHead(
            index.Name ?? Path.GetFileNameWithoutExtension(mrpackPath),
            index.VersionId,
            index.Game,
            index.Dependencies?.Minecraft,
            index.Dependencies?.FabricLoader);
    }

    /// <summary>导入展示用的实例版本：与发布器 normalizePackVersion 同规——
    /// 结尾 <c>-full</c>（大小写不敏感）去掉，如 <c>Qinglan 2.0-full</c> → <c>Qinglan 2.0</c>。</summary>
    public static string NormalizeVersionId(string? versionId)
    {
        var version = string.IsNullOrWhiteSpace(versionId) ? "?" : versionId.Trim();
        var dash = version.ToLowerInvariant().LastIndexOf("-full", StringComparison.Ordinal);
        return dash > 0 ? version[..dash] : version;
    }

    /// <summary>mrpack 元数据头（ReadHead 的返回）。</summary>
    public sealed record MrpackHead(string Name, string? VersionId, string? Game,
        string? Minecraft, string? FabricLoader);

    private static ZipArchiveEntry ReadIndexEntry(ZipArchive zip)
    {
        var entries = zip.Entries.Where(e =>
            e.FullName.Replace('\\', '/').Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count != 1)
            throw new IOException("mrpack 里 modrinth.index.json 数量异常: " + entries.Count);
        return entries[0];
    }

    private static readonly JsonSerializerOptions JsonOpts = new();

    public sealed class MrpackIndex
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
        [JsonPropertyName("game")] public string? Game { get; set; }
        [JsonPropertyName("versionId")] public string? VersionId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("files")] public List<MrpackFile> Files { get; set; } = new();
        [JsonPropertyName("dependencies")] public MrpackDependencies? Dependencies { get; set; }
    }

    public sealed class MrpackFile
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
        [JsonPropertyName("hashes")] public MrpackHashes? Hashes { get; set; }
        [JsonPropertyName("env")] public MrpackEnv? Env { get; set; }
        [JsonPropertyName("downloads")] public List<string> Downloads { get; set; } = new();
        [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    }

    public sealed class MrpackHashes
    {
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("sha512")] public string? Sha512 { get; set; }
    }

    public sealed class MrpackEnv
    {
        [JsonPropertyName("client")] public string? Client { get; set; }
        [JsonPropertyName("server")] public string? Server { get; set; }
    }

    public sealed class MrpackDependencies
    {
        [JsonPropertyName("minecraft")] public string? Minecraft { get; set; }
        [JsonPropertyName("fabric-loader")] public string? FabricLoader { get; set; }
    }

    // ───────────────────────── QL 化（种子） ─────────────────────────

    /// <summary>种子导入摘要（Java 语义同名保留）。</summary>
    public sealed class SeedOutcome
    {
        public int Seeded;
        public int Mismatch;
        public int Missing;
    }

    /// <summary>QL 化一个已安装的游戏目录：布局愈合 + tracked 登记 + 状态落盘 + dblpack。</summary>
    /// <remarks>内容不匹配的文件故意留下且不 tracked——交给下一轮 sync 重下覆盖。</remarks>
    public static SeedOutcome Seedize(string gameDir, QlManifest manifest, string? manifestUrl,
        string targetChannel, Dictionary<string, bool>? optionalExtras = null,
        Action<int, int>? listener = null, CancellationToken ct = default)
    {
        var channel = QlManifest.ModsChannels.Contains(targetChannel) ? targetChannel : "std";

        var state = QlSyncState.Empty();
        state.ActiveChannel = channel;
        state.ManifestUrl = manifestUrl;
        if (optionalExtras is not null)
        {
            foreach (var (group, enable) in optionalExtras)
                state.SetOptionalEnabled(group, enable);
        }
        var enabledOptional = state.EnabledOptionalGroups();

        // 布局先行：recover 负责盖标记和恢复半僵尸状态。磁盘上已有标记的目录赢过种子目标，
        // 重跑（再导一次）也永远不会重盖现有频道或把别的内容搅进当前 folder。
        var detected = QlDirectoryLayout.DetectActiveChannel(gameDir, channel);
        QlDirectoryLayout.Recover(gameDir, detected ?? channel);
        channel = detected ?? channel;
        state.ActiveChannel = channel;

        var outcome = new SeedOutcome();

        int total = 0;
        foreach (var modsChannel in QlManifest.ModsChannels)
            total += manifest.ChannelOrThrow(modsChannel).Mods.Count;
        total += manifest.ChannelOrThrow(QlManifest.ChannelCommon).Files.Count;

        int finished = 0;

        var tracked = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        // --- mods 频道：只有已存在的 folder 才能被种。 --------------------------------
        foreach (var modsChannel in QlManifest.ModsChannels)
        {
            var folder = QlDirectoryLayout.ModsDirectory(gameDir, modsChannel, channel);
            if (!Directory.Exists(folder))
            {
                total -= manifest.ChannelOrThrow(modsChannel).Mods.Count;
                continue; // 停用频道在下一轮 sync 里由引擎下载。
            }
            foreach (var entry in manifest.ChannelOrThrow(modsChannel).Mods)
            {
                var raw = RawFileName(entry);
                var enabled = string.IsNullOrWhiteSpace(entry.Optional) || enabledOptional.Contains(entry.Optional!);
                var target = Path.Combine(folder, enabled ? raw : raw + QlConstants.DisabledSuffix);
                var status = ReconcileForSeedSoftly(target, entry, ct);
                if (status == SeedStatus.Seeded)
                {
                    if (!tracked.TryGetValue(modsChannel, out var map))
                        tracked[modsChannel] = map = new(StringComparer.Ordinal);
                    map[entry.Path!] = entry.Sha1!;
                }
                Record(outcome, status);
                finished++;
                listener?.Invoke(finished, total);
            }
        }

        // --- common 频道：普通游戏目录路径。 ------------------------------------------
        foreach (var entry in manifest.ChannelOrThrow(QlManifest.ChannelCommon).Files)
        {
            var target = Path.Combine(gameDir, entry.Path!.Replace('/', Path.DirectorySeparatorChar));
            var status = ReconcileForSeedSoftly(target, entry, ct);
            if (status == SeedStatus.Seeded)
            {
                if (!tracked.TryGetValue(QlManifest.ChannelCommon, out var map))
                    tracked[QlManifest.ChannelCommon] = map = new(StringComparer.Ordinal);
                map[entry.Path!] = entry.Sha1!;
            }
            Record(outcome, status);
            finished++;
            listener?.Invoke(finished, total);
        }

        state.ReplaceTracked(tracked);
        state.PackVersion = manifest.PackVersionOrDefault();
        state.Save(gameDir);

        // dblpack 清单 best-effort：失败不中断导入。
        try
        {
            QlDblPack.Scan(gameDir, manifest, channel, enabledOptional).Save(gameDir);
        }
        catch (Exception e)
        {
            Globals.Log("dblpack 生成失败(忽略): " + e.Message);
        }
        return outcome;
    }

    private static void Record(SeedOutcome outcome, SeedStatus status)
    {
        switch (status)
        {
            case SeedStatus.Seeded: outcome.Seeded++; break;
            case SeedStatus.Mismatch: outcome.Mismatch++; break;
            case SeedStatus.Missing: outcome.Missing++; break;
        }
    }

    /// <summary>柔和对账：文件读不动（坏包、占用）时按 mismatch 记账，绝不让整个导入失败。</summary>
    private static SeedStatus ReconcileForSeedSoftly(string target, QlManifest.FileEntry entry, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            return ReconcileForSeed(target, entry);
        }
        catch (IOException e)
        {
            Globals.Log("种子文件无法哈希（等 sync 替换）: " + target + " -> " + e.Message);
            return SeedStatus.Mismatch;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
    }

    private static SeedStatus ReconcileForSeed(string target, QlManifest.FileEntry entry)
    {
        var expectedSha = entry.Sha1 ?? throw new ManifestException("manifest sha missing");
        if (!File.Exists(target)) return SeedStatus.Missing;
        var sizeOk = entry.Size is null || new FileInfo(target).Length == entry.Size.Value;
        if (sizeOk && QlHttp.Sha1Hex(target).Equals(expectedSha, StringComparison.Ordinal))
            return SeedStatus.Seeded;
        Globals.Log("种子文件内容不匹配（等 sync 替换）: " + target);
        return SeedStatus.Mismatch;
    }

    private static string RawFileName(QlManifest.FileEntry entry)
    {
        var p = entry.Path ?? throw new ManifestException("manifest path missing");
        return p.Substring(p.IndexOf('/', 1) + 1);
    }

    private enum SeedStatus { Seeded, Mismatch, Missing }
}
