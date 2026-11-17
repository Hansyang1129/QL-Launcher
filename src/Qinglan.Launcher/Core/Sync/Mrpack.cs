using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qinglan.Launcher.Core;

/// <summary>mrpack（Modrinth 整合包格式）的只读视图：索引模型、头部预览与版本名归一。</summary>
/// <para>新版同步方式里更新源就是包文件本身；这里只负责「读懂包」，落盘交给 <see cref="QlPackSync"/>。</para>
public static class QlMrpack
{
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

    /// <summary>展示用的实例版本：与发布器 normalizePackVersion 同规——
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

    internal static ZipArchiveEntry ReadIndexEntry(ZipArchive zip)
    {
        var entries = zip.Entries.Where(e =>
            e.FullName.Replace('\\', '/').Equals("modrinth.index.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (entries.Count != 1)
            throw new IOException("mrpack 里 modrinth.index.json 数量异常: " + entries.Count);
        return entries[0];
    }

    internal static readonly JsonSerializerOptions JsonOpts = new();

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

        /// <summary>旧字段名兼容（QLManifestTool.longGet 先 fileSize 后 size）。</summary>
        [JsonPropertyName("size")] public long Size { get; set; }
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
}
