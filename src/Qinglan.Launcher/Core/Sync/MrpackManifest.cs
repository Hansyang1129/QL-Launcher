using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qinglan.Launcher.Core;

/// <summary>把一份 .mrpack 现场构建成 <see cref="QlManifest"/>（QLManifestTool.buildFromMrpack 同规）。</summary>
/// <para>FULL 包（载荷全在 overrides/ 下、index 文件列表为空）与 LITE 包（index 指向
/// Modrinth/Forge CDN 地址）都接受：index 条目的 CDN 地址在前、资源服务器兜底地址在后；
/// overrides 内嵌条目只带兜底地址。</para>
/// <para>路径按首段路由：<c>mods/**</c> 进 std 频道，<c>config|shaderpacks|resourcepacks/**</c>
/// 与 <c>options.txt</c> 进 common 频道，其余跳过并记账。RPU 客户端独占的路径
/// （<c>qlrpu/**</c>、<c>config/qlrpu-client.json</c>）绝不发布；resourcepacks 照常发布。</para>
public static class QlMrpackManifest
{
    /// <summary>未指定时的包名（QLManifestTool.DEFAULT_PACK_NAME）。</summary>
    public const string DefaultPackName = "Qinglan-2nd";

    /// <summary>未指定时的包版本（QLManifestTool.DEFAULT_PACK_VERSION）。</summary>
    public const string DefaultPackVersion = "26.8.1";

    /// <summary>作为 common 文件分发的 overrides 目录：mod 配置、光影与可选资源包。</summary>
    private static readonly string[] CommonFolders = { "config", "shaderpacks", "resourcepacks" };

    /// <summary>作为 common 文件分发的 overrides 根文件。</summary>
    private static readonly string[] CommonRootFiles = { "options.txt" };

    /// <summary>overrides 载荷里永不发布的根文件。</summary>
    private static readonly string[] SkippedRootFiles = { "servers.dat", "modrinth.index.json" };

    /// <summary>overrides 载荷里永不发布的根目录。</summary>
    private static readonly string[] SkippedRootFolders = { "PCL" };

    /// <summary>不发布的 mod 文件前缀（整合包里已知的坏遗留）。</summary>
    private static readonly string[] ExcludedModFilePrefixes = { "fabric-loader-", "fabric-api" };

    /// <summary>从 mrpack 构建内存 manifest 对象。</summary>
    /// <exception cref="IOException">zip 打不开时。</exception>
    public static QlManifest BuildFromMrpack(string mrpackPath)
    {
        var baseUrl = QlConstants.PublisherFallbackBaseUrl.TrimEnd('/') + "/";
        var packName = DefaultPackName;
        var packVersion = DefaultPackVersion;

        var knownPaths = new HashSet<string>(StringComparer.Ordinal);
        var stdMods = new List<QlManifest.FileEntry>();
        var commonFiles = new List<QlManifest.FileEntry>();
        int rpuCount = 0, skippedCount = 0, coveredByIndex = 0;

        using (var zip = ZipFile.OpenRead(mrpackPath))
        {
            var indexEntry = zip.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals("modrinth.index.json", StringComparison.Ordinal));
            if (indexEntry != null)
            {
                QlMrpack.MrpackIndex index;
                using (var s = indexEntry.Open())
                {
                    index = JsonSerializer.Deserialize<QlMrpack.MrpackIndex>(s, QlMrpack.JsonOpts)
                            ?? new QlMrpack.MrpackIndex();
                }
                if (!string.IsNullOrWhiteSpace(index.Name)) packName = index.Name.Trim();
                packVersion = NormalizePackVersion(
                    string.IsNullOrWhiteSpace(index.VersionId) ? packVersion : index.VersionId.Trim());
                CollectIndexEntries(index, baseUrl, stdMods, commonFiles, knownPaths,
                    ref rpuCount, ref skippedCount);
            }
            CollectOverrideEntries(zip, baseUrl, stdMods, commonFiles, knownPaths,
                ref rpuCount, ref skippedCount, ref coveredByIndex);
        }

        return Render(packName, packVersion, stdMods, commonFiles, rpuCount, skippedCount, coveredByIndex);
    }

    // ───────────────────────── index 条目 ─────────────────────────

    private static void CollectIndexEntries(QlMrpack.MrpackIndex index, string baseUrl,
        List<QlManifest.FileEntry> stdMods, List<QlManifest.FileEntry> commonFiles,
        HashSet<string> knownPaths, ref int rpuCount, ref int skippedCount)
    {
        // FULL 包：载荷在 overrides 里，index 文件列表为空是完全正常的。
        foreach (var file in index.Files)
        {
            var path = file.Path.Replace('\\', '/');
            var sha1 = IndexSha1Of(file);
            if (string.IsNullOrEmpty(path) || sha1 == null || IsUnsafeRelativePath(path))
            {
                skippedCount++;
                continue;
            }
            if (QlManifest.IsRpuOwned(path))
            {
                rpuCount++; // 游戏内 RPU 客户端独占，无论它住在哪个目录
                continue;
            }
            var route = ChannelOf(path);
            if (route == null)
            {
                skippedCount++; // 未路由
                continue;
            }
            if (IsExcludedModPath(path))
            {
                skippedCount++;
                continue;
            }
            if (!knownPaths.Add(path))
            {
                skippedCount++; // index 内部重复：同一文件绝不发布第二次
                continue;
            }

            var urls = new List<string>(file.Downloads) { baseUrl + EncodeUrlPath(path) };
            Add(route, path, sha1, file.FileSize != 0 ? file.FileSize : file.Size, urls,
                stdMods, commonFiles);
        }
    }

    // ───────────────────────── overrides 条目 ─────────────────────────

    private static void CollectOverrideEntries(ZipArchive zip, string baseUrl,
        List<QlManifest.FileEntry> stdMods, List<QlManifest.FileEntry> commonFiles,
        HashSet<string> knownPaths, ref int rpuCount, ref int skippedCount, ref int coveredByIndex)
    {
        // 按条目名排序遍历，输出与 Java 工具同序（稳定、可 diff）。
        var entries = zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.FullName) && !e.FullName.EndsWith('/'))
            .OrderBy(e => e.FullName, StringComparer.Ordinal)
            .ToList();

        foreach (var zipEntry in entries)
        {
            var name = zipEntry.FullName.Replace('\\', '/');
            if (!name.StartsWith("overrides/", StringComparison.Ordinal)) continue;
            var relative = name["overrides/".Length..];

            if (IsSkippedPath(relative))
            {
                skippedCount++;
                continue;
            }
            if (QlManifest.IsRpuOwned(relative))
            {
                rpuCount++;
                continue;
            }
            var route = ChannelOf(relative);
            if (route == null)
            {
                skippedCount++;
                continue;
            }
            if (IsExcludedModPath(relative))
            {
                skippedCount++;
                continue;
            }
            if (!knownPaths.Add(relative))
            {
                coveredByIndex++; // index 已发布：同步从 CDN 拉，不嵌进清单
                continue;
            }

            var (sha1, size) = HashEntry(zipEntry);
            Add(route, relative, sha1, size, new List<string> { baseUrl + EncodeUrlPath(relative) },
                stdMods, commonFiles);
        }
    }

    // ───────────────────────── 条目组装 ─────────────────────────

    private static void Add(string channel, string path, string sha1, long size, List<string> urls,
        List<QlManifest.FileEntry> stdMods, List<QlManifest.FileEntry> commonFiles)
    {
        // 只有 mod 进可选组；可选运行时的 config（如 DistantHorizons.toml）照常发布，
        // 这样组被切换后不会有陈旧配置残留。
        var entry = new QlManifest.FileEntry
        {
            Path = path,
            Sha1 = sha1,
            Size = size,
            Urls = urls,
            Optional = channel == QlManifest.ChannelMods ? OptionalGroupOf(path) : null,
        };
        if (channel == QlManifest.ChannelMods) stdMods.Add(entry);
        else commonFiles.Add(entry);
    }

    private static QlManifest Render(string packName, string packVersion,
        List<QlManifest.FileEntry> stdMods, List<QlManifest.FileEntry> commonFiles,
        int rpuCount, int skippedCount, int coveredByIndex)
    {
        Globals.Log($"mrpack 清单: std={stdMods.Count} common={commonFiles.Count} " +
                    $"rpu={rpuCount} skipped={skippedCount} coveredByIndex={coveredByIndex}");

        return new QlManifest
        {
            Schema = QlManifest.SchemaVersion,
            Pack = new QlManifest.PackInfo
            {
                Name = packName,
                Version = packVersion,
                UpdatedAt = DateTime.UtcNow.ToString("o"),
            },
            Channels = new Dictionary<string, QlManifest.Channel>(StringComparer.Ordinal)
            {
                [QlManifest.ChannelMods] = new() { Label = "官方包", Mods = stdMods, Files = new() },
                [QlManifest.ChannelCommon] = new() { Label = "公共文件", Mods = new(), Files = commonFiles },
            },
            Servers = new List<QlManifest.QlServer>
            {
                new() { Id = "qinglan-main", Name = "轻蓝服务器", Address = "mc.example.com:32650", Kind = "minecraft" },
                new() { Id = "mtr-web", Name = "MTR 地图", Address = "mc.example.com:14614", Kind = "mtr-web" },
                new() { Id = "mtr-reporter", Name = "MTR 数据", Address = "mc.example.com:19451", Kind = "mtr-reporter" },
            },
        };
    }

    // ───────────────────────── 判定与工具 ─────────────────────────

    /// <summary>包相对路径的 manifest 频道；未路由返回 null。</summary>
    public static string? ChannelOf(string relative)
    {
        var slash = relative.IndexOf('/');
        var segment = slash < 0 ? relative : relative[..slash];
        if (segment == "mods") return QlManifest.ChannelMods;
        if (CommonFolders.Contains(segment, StringComparer.Ordinal)
            || CommonRootFiles.Contains(segment, StringComparer.Ordinal))
            return QlManifest.ChannelCommon;
        return null;
    }

    private static bool IsSkippedPath(string relative)
    {
        var slash = relative.IndexOf('/');
        var segment = slash < 0 ? relative : relative[..slash];
        return SkippedRootFolders.Contains(segment, StringComparer.Ordinal)
               || SkippedRootFiles.Contains(relative, StringComparer.Ordinal);
    }

    private static bool IsExcludedModPath(string path)
    {
        if (!path.StartsWith("mods/", StringComparison.Ordinal)) return false;
        var fileName = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        return ExcludedModFilePrefixes.Any(fileName.StartsWith);
    }

    /// <summary>mod 文件的可选组名；必备 mod 返回 null。</summary>
    public static string? OptionalGroupOf(string path)
    {
        var fileName = path[(path.LastIndexOf('/') + 1)..].ToLowerInvariant();
        if (fileName.Contains("voxy")) return "voxy";
        if (fileName.Contains("distant") && fileName.Contains("horizon")) return "dh";
        if (fileName.Contains("axiom")) return "axiom";
        return null;
    }

    private static bool IsUnsafeRelativePath(string path)
        => string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\')
           || path.Contains("..") || path.Contains(':');

    /// <summary>index 声明的包版本：剥结尾 -full（大小写不敏感），空白回落默认值。</summary>
    public static string NormalizePackVersion(string? requested)
    {
        var version = string.IsNullOrWhiteSpace(requested) ? DefaultPackVersion : requested.Trim();
        var dash = version.ToLowerInvariant().LastIndexOf("-full", StringComparison.Ordinal);
        return dash > 0 ? version[..dash] : version;
    }

    /// <summary>把包相对路径的每一段百分号编码为可作 HTTP URL 路径的形式；保留 / 分隔。</summary>
    public static string EncodeUrlPath(string path)
        => string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>index 条目的 hashes.sha1；必须是 40 位才算数（小写归一；与 Java 工具同规不查字符集）。</summary>
    private static string? IndexSha1Of(QlMrpack.MrpackFile file)
    {
        var sha1 = file.Hashes?.Sha1;
        return sha1 != null && sha1.Length == 40 ? sha1.ToLowerInvariant() : null;
    }

    /// <summary>边读边算 SHA-1 与字节数（流式，避免整文件进内存）。</summary>
    private static (string Sha1, long Size) HashEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var sha = System.Security.Cryptography.SHA1.Create();
        var buffer = new byte[64 * 1024];
        long size = 0;
        int count;
        while ((count = s.Read(buffer, 0, buffer.Length)) > 0)
        {
            sha.TransformBlock(buffer, 0, count, null, 0);
            size += count;
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (Convert.ToHexString(sha.Hash!).ToLowerInvariant(), size);
    }
}
