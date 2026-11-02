using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qinglan.Launcher.Core;

/// <summary>收集 manifest 校验过程中可预期的失败。</summary>
public sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

/// <summary>轻蓝整合包的期望远端状态（manifest v1，与 ql-launcher 发布器互通）。</summary>
/// <para>启动器拿它对账磁盘文件，只做最小动作（下载 / 重命名 / 删除）。</para>
public sealed class QlManifest
{
    /// <summary>本启动器支持的 schema 版本，必须一致。</summary>
    public const int SchemaVersion = 1;

    /// <summary>拥有独立 mods 文件夹的频道：活跃 = mods，停用 = mods-<name>。</summary>
    public static readonly string[] ModsChannels = { "std", "pfm" };

    /// <summary>共享频道名：文件直接相对游戏目录解析。</summary>
    public const string ChannelCommon = "common";

    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("pack")] public PackInfo? Pack { get; set; }
    [JsonPropertyName("channels")] public Dictionary<string, Channel>? Channels { get; set; }
    [JsonPropertyName("servers")] public List<QlServer>? Servers { get; set; }

    /// <summary>整合包身份：名称、版本、发布时间。</summary>
    public sealed class PackInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("updatedAt")] public string? UpdatedAt { get; set; }
    }

    /// <summary>一起安装/移除的一组文件。</summary>
    public sealed class Channel
    {
        [JsonPropertyName("label")] public string? Label { get; set; }
        [JsonPropertyName("mods")] public List<FileEntry> Mods { get; set; } = new();
        [JsonPropertyName("files")] public List<FileEntry> Files { get; set; } = new();
    }

    /// <summary>一个被管理的文件。</summary>
    public sealed class FileEntry
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("urls")] public List<string> Urls { get; set; } = new();
        [JsonPropertyName("optional")] public string? Optional { get; set; }
    }

    /// <summary>预留的服务器描述（同步引擎不消费）。</summary>
    public sealed class QlServer
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("address")] public string? Address { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
    }

    /// <summary>整合包名（缺省 Qinglan）。</summary>
    public string PackNameOrDefault() => !string.IsNullOrWhiteSpace(Pack?.Name) ? Pack!.Name! : "Qinglan";

    /// <summary>整合包版本（缺省 ?）。</summary>
    public string PackVersionOrDefault() => !string.IsNullOrWhiteSpace(Pack?.Version) ? Pack!.Version! : "?";

    /// <summary>校验过的频道表；缺失即抛。</summary>
    public Dictionary<string, Channel> ChannelsOrThrow()
        => Channels is { Count: > 0 } c
            ? c
            : throw new ManifestException("Manifest does not contain channels");

    /// <summary>按名取频道；不存在即抛。</summary>
    public Channel ChannelOrThrow(string name)
        => ChannelsOrThrow().TryGetValue(name, out var ch)
            ? ch
            : throw new ManifestException("Manifest does not contain channel " + name);

    /// <summary>声明过的可选组，按声明顺序（先 mods 后 files）。</summary>
    public List<string> OptionalGroups()
    {
        var groups = new List<string>();
        foreach (var ch in ChannelsOrThrow().Values)
        {
            CollectGroups(ch.Mods, groups);
            CollectGroups(ch.Files, groups);
        }
        return groups;
    }

    private static void CollectGroups(List<FileEntry> entries, List<string> sink)
    {
        foreach (var e in entries)
            if (!string.IsNullOrWhiteSpace(e.Optional) && !sink.Contains(e.Optional!))
                sink.Add(e.Optional!);
    }

    /// <summary>某通道里不受禁用可选组影响的条目。</summary>
    public List<FileEntry> EffectiveEntries(string channelName, ISet<string> enabledOptional)
    {
        var ch = ChannelOrThrow(channelName);
        return Effective(ModsChannels.Contains(channelName) ? ch.Mods : ch.Files, enabledOptional);
    }

    private static List<FileEntry> Effective(List<FileEntry> entries, ISet<string> enabledOptional)
    {
        var result = new List<FileEntry>(entries.Count);
        foreach (var e in entries)
            if (string.IsNullOrWhiteSpace(e.Optional) || enabledOptional.Contains(e.Optional!))
                result.Add(e);
        return result;
    }

    /// <summary>未知频道名归一成 common。</summary>
    public static string NormalizeChannel(string channel)
        => ModsChannels.Contains(channel.ToLowerInvariant()) ? channel.ToLowerInvariant() : ChannelCommon;

    /// <summary>解析并严格校验 manifest 文本。</summary>
    /// <exception cref="ManifestException">schema 不符 / 必需频道缺失 / 条目不完整 / 危险路径 / 渠道内重名。</exception>
    public static QlManifest FromJson(string json)
    {
        QlManifest? m;
        try
        {
            m = JsonSerializer.Deserialize<QlManifest>(json);
        }
        catch (JsonException e)
        {
            throw new ManifestException("Manifest is not valid JSON: " + e.Message);
        }
        if (m == null) throw new ManifestException("Manifest is empty");
        if (m.Schema != SchemaVersion)
            throw new ManifestException("Unsupported manifest schema: " + m.Schema);
        var channels = m.Channels;
        if (channels == null || channels.Count == 0)
            throw new ManifestException("Manifest does not contain channels");
        foreach (var required in new[] { "std", "pfm", "common" })
            if (!channels.ContainsKey(required))
                throw new ManifestException("Manifest is missing channel " + required);

        foreach (var (name, ch) in channels)
            ValidateChannel(name, ch);
        return m;
    }

    private static void ValidateChannel(string channelName, Channel channel)
    {
        var dedupe = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        ValidateEntries(channelName, channel.Mods, dedupe);
        ValidateEntries(channelName, channel.Files, dedupe);
    }

    private static void ValidateEntries(string channelName, List<FileEntry> entries,
        Dictionary<string, FileEntry> dedupe)
    {
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Path) || string.IsNullOrEmpty(e.Sha1) || e.Urls.Count == 0)
                throw new ManifestException("Manifest entry is incomplete: " + channelName + "/" + e.Path);
            var p = e.Path!;
            if (p.StartsWith('/') || p.StartsWith('\\') || p.Contains("..") || p.Contains(':'))
                throw new ManifestException("Unsafe manifest path: " + p);
            if (e.Size is null or < 0)
                throw new ManifestException("Manifest entry " + channelName + "/" + p
                    + " declares no valid size: " + e.Size);
            if (!dedupe.TryAdd(p, e))
                throw new ManifestException("Duplicate manifest path: " + p);
        }
    }
}
