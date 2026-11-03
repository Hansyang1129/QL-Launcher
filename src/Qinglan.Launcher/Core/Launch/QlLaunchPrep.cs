using System.Runtime.InteropServices;
using System.IO.Compression;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>一个待下载文件：URL 候选、落盘绝对路径、SHA-1 与期望大小。</summary>
public sealed record QlFileTask(string Label, string[] Urls, string Target, string Sha1, long Size);

/// <summary>准备完成的启动套餐：类路径、natives、参数模板等，喂给 ArgumentsBuilder / 进程器。</summary>
public sealed class QlPrepResult
{
    public string VersionId { get; init; } = "";
    public string MainClass { get; init; } = "";
    public string GameJar { get; init; } = "";
    public List<string> Classpath { get; init; } = new();
    public string NativesDir { get; init; } = "";
    public string AssetsDir { get; init; } = "";
    public string AssetIndexName { get; init; } = "";
    public List<QlArg> JvmArgs { get; init; } = new();
    public List<QlArg> GameArgs { get; init; } = new();
    public int JavaMajor { get; init; }
    public string? LogConfigFile { get; init; }
}

/// <summary>
/// 启动准备：合并版本 JSON → 按“缺才下”规划 → 并行下载（SHA-1 落盘校验）→ 解 natives。
/// 版本隔离：所有内容都落在游戏目录内（versions/ libraries/ assets/），实例互不串扰。
/// </summary>
public static class QlLaunchPrep
{
    public const int Parallelism = 8;
    public const string VanillaMetaUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public const string FabricProfileTpl = "https://meta.fabricmc.net/v2/versions/loader/{game}/{loader}/profile/json";
    public const string AssetBaseDefault = "https://resources.download.minecraft.net/";

    // ───────────────────── 地址解析 ─────────────────────

    /// <summary>真实 meta 网址解析：1.20.1 → vanilla json；loader 版本 → fabric profile。</summary>
    public static async Task<(string VanillaUrl, string FabricUrl)> ResolveAsync(
        string gameVersion, string loaderVersion, CancellationToken ct = default)
    {
        var text = await QlHttp.GetTextAsync(VanillaMetaUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        string? found = null;
        if (doc.RootElement.TryGetProperty("versions", out var versions))
            foreach (var v in versions.EnumerateArray())
                if (v.GetProperty("id").GetString() == gameVersion)
                {
                    found = v.GetProperty("url").GetString();
                    break;
                }
        if (found is null)
            throw new IOException("Mojang 版本表中找不到 " + gameVersion);
        return (found, FabricProfileTpl.Replace("{game}", gameVersion).Replace("{loader}", loaderVersion));
    }

    /// <summary>下载两份 json 并合并（fabric 挂 vanilla）。</summary>
    public static async Task<QlVersionJson> MergeAsync(string vanillaUrl, string fabricUrl,
        CancellationToken ct = default)
    {
        var van = QlVersionJson.Parse(await QlHttp.GetTextAsync(vanillaUrl, ct).ConfigureAwait(false));
        var fab = QlVersionJson.Parse(await QlHttp.GetTextAsync(fabricUrl, ct).ConfigureAwait(false));
        return QlVersionJson.Merge(van, fab);
    }

    // ───────────────────── 规划 ─────────────────────

    /// <summary>列出所有缺失（大小不符视为缺）的文件任务。资源索引 json 在此内联读取以便枚举对象。</summary>
    public static async Task<List<QlFileTask>> PlanAsync(QlVersionJson m, string gameDir,
        string? assetUrlBase = null, CancellationToken ct = default)
    {
        var tasks = new List<QlFileTask>();

        void Plan(string label, string[] urls, string target, string sha1, long size)
        {
            if (string.IsNullOrEmpty(sha1)) return; // 无校验值的不接（v1 规约：全部候选必须有 SHA-1）
            var fi = new FileInfo(target);
            if (fi.Exists && fi.Length == size) return; // 大小一致 = 已就位；SHA-1 由下载器写后强校验兜底
            tasks.Add(new QlFileTask(label, urls, target, sha1, size));
        }

        if (m.ClientUrl is not null)
            Plan("客户端 jar", new[] { m.ClientUrl }, VersionJarPath(gameDir, m.Id), m.ClientSha1 ?? "", m.ClientSize);

        var libsDir = Path.Combine(gameDir, "libraries");
        var archWant = NativeClassifierForArch();
        foreach (var lib in m.Libraries)
        {
            // 本机架构不符的 natives（x64 上遇 arm64/x86 变体）不下载也不上类路径。
            if (lib.IsNativeWindows &&
                !QlVersionJson.SplitName(lib.Name).Classifier.Equals(archWant, StringComparison.Ordinal))
                continue;
            // windows 上跳过 linux/osx 专属库（规则表 last-match-wins）。
            if (!QlRules.Evaluate(lib.Rules, "windows", ArchLabel(), null)) continue;
            Plan("库 " + lib.Path, lib.Urls, Path.Combine(libsDir, lib.Path.Replace('/', Path.DirectorySeparatorChar)),
                lib.Sha1, lib.Size);
        }

        string logFile = "";
        if (m.LogFileUrl is not null)
        {
            logFile = Path.Combine(gameDir, "assets", "log_configs", m.LogFileId ?? "log4j.xml");
            Plan("log4j 配置", new[] { m.LogFileUrl }, logFile, m.LogFileSha1 ?? "", m.LogFileSize);
        }

        // 资源索引：小文件，这里内联确认（缺失就下、损坏重下一次）；成功后才能枚举对象。
        var indexName = m.AssetIndexId ?? throw new IOException("版本 JSON 缺 assetIndex.id");
        var indexPath = Path.Combine(gameDir, "assets", "indexes", indexName + ".json");

        async Task FetchIndex()
        {
            if (string.IsNullOrEmpty(m.AssetIndexUrl))
                throw new IOException("资源索引缺失且无下载地址: " + indexPath);
            try { File.Delete(indexPath); } catch { /* 目标不在 */ }
            if (!await QlHttp.DownloadToFileAsync(new[] { m.AssetIndexUrl! }, indexPath,
                    m.AssetIndexSha1 ?? "", ct).ConfigureAwait(false))
                throw new IOException("资源索引下载失败: " + indexPath);
        }

        var fi0 = new FileInfo(indexPath);
        if (!(fi0.Exists && (m.AssetIndexSize <= 0 || fi0.Length == m.AssetIndexSize)))
            await FetchIndex();

        string indexText;
        try
        {
            indexText = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
            using var probe = JsonDocument.Parse(indexText); // 顺手体检：坏索引当场重下
        }
        catch (JsonException)
        {
            await FetchIndex();
            indexText = await File.ReadAllTextAsync(indexPath, ct).ConfigureAwait(false);
        }
        var @base = assetUrlBase ?? AssetBaseDefault;
        using var idoc = JsonDocument.Parse(indexText);
        var objectsDir = Path.Combine(gameDir, "assets", "objects");
        if (idoc.RootElement.TryGetProperty("objects", out var objs))
            foreach (var o in objs.EnumerateObject())
            {
                var hash = o.Value.GetProperty("hash").GetString() ?? "";
                var size = o.Value.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
                var h2 = hash[..Math.Min(2, hash.Length)];
                Plan("资源 " + o.Name,
                    new[] { @base.TrimEnd('/') + "/" + h2 + "/" + hash },
                    Path.Combine(objectsDir, h2, hash), hash, size);
            }
        return tasks;
    }

    // ───────────────────── 下载 ─────────────────────

    /// <summary>8 路并发下载；有终败即抛 IOException（列前几条标签），取消正常传递。</summary>
    public static async Task DownloadAllAsync(IEnumerable<QlFileTask> tasks, Action<int, int>? progress,
        CancellationToken ct)
    {
        var list = tasks.ToList();
        if (list.Count == 0) return;
        var failures = new ConcurrentBag<string>();
        int done = 0, total = list.Count;
        await Parallel.ForEachAsync(list,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (t, token) =>
            {
                var ok = await QlHttp.DownloadToFileAsync(t.Urls, t.Target, t.Sha1, token).ConfigureAwait(false);
                if (!ok) failures.Add(t.Label);
                progress?.Invoke(Interlocked.Increment(ref done), total);
            }).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            var head = string.Join("; ", failures.Take(4));
            throw new IOException("启动文件下载失败 " + failures.Count + " 个: " + head +
                                  (failures.Count > 4 ? " …" : ""));
        }
    }

    // ───────────────────── 收尾 ─────────────────────

    /// <summary>解 natives、组类路径，产出供参数生成/进程启动的套餐。幂等（重复调用只重解压）。</summary>
    public static QlPrepResult Finalize(QlVersionJson m, string gameDir)
    {
        var nativesDir = Path.Combine(gameDir, "natives-windows");
        Directory.CreateDirectory(nativesDir);
        var libsDir = Path.Combine(gameDir, "libraries");
        var archWant = NativeClassifierForArch();

        var usable = m.Libraries.Where(lib =>
            {
                // 与 PlanAsync 同一套过滤：windows 规则 + 本机架构 natives。
                if (lib.IsNativeWindows &&
                    !QlVersionJson.SplitName(lib.Name).Classifier.Equals(archWant, StringComparison.Ordinal))
                    return false;
                return QlRules.Evaluate(lib.Rules, "windows", ArchLabel(), null);
            })
            .ToList();

        foreach (var lib in usable.Where(l => l.IsNativeWindows))
        {
            var jar = Path.Combine(libsDir, lib.Path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(jar)) continue;
            using var zip = ZipFile.OpenRead(jar);
            foreach (var entry in zip.Entries)
            {
                var ext = Path.GetExtension(entry.FullName);
                if (ext is not (".dll" or ".exe")) continue;
                var outPath = Path.Combine(nativesDir, Path.GetFileName(entry.FullName));
                using var src = entry.Open();
                using var dst = File.Create(outPath);
                src.CopyTo(dst);
            }
        }

        var classpath = usable
            .Select(lib => Path.Combine(libsDir, lib.Path.Replace('/', Path.DirectorySeparatorChar)))
            .ToList();
        classpath.Add(VersionJarPath(gameDir, m.Id));

        return new QlPrepResult
        {
            VersionId = m.Id,
            MainClass = m.MainClass,
            GameJar = VersionJarPath(gameDir, m.Id),
            Classpath = classpath,
            NativesDir = nativesDir,
            AssetsDir = Path.Combine(gameDir, "assets"),
            AssetIndexName = m.AssetIndexId ?? "",
            JvmArgs = m.JvmArgs.ToList(),
            GameArgs = m.GameArgs.ToList(),
            JavaMajor = m.JavaMajor,
            LogConfigFile = m.LogFileId is null
                ? null
                : Path.Combine(gameDir, "assets", "log_configs", m.LogFileId),
        };
    }

    /// <summary>Mojang 规则表使用的“本机架构”记号（v1 目标机器即 x64）。</summary>
    public static string ArchLabel()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };

    /// <summary>本机架构对应的 natives 分类器（x64 → natives-windows）。</summary>
    public static string NativeClassifierForArch()
        => RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "natives-windows-arm64",
            Architecture.X86 => "natives-windows-x86",
            _ => "natives-windows",
        };

    public static string VersionJarPath(string gameDir, string versionId)
        => Path.Combine(gameDir, "versions", versionId, versionId + ".jar");
}
