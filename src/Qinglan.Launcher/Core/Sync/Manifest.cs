using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qinglan.Launcher.Core;

/// <summary>收集 manifest 校验过程中可预期的失败。</summary>
public sealed class ManifestException : Exception
{
    public ManifestException(string message) : base(message) { }
}

/// <summary>轻蓝整合包的期望远端状态（manifest v1，与 ql-launcher 发布器互通）。</summary>
/// <para>启动器拿它对账磁盘文件，只做最小动作（下载 / 重命名 / 删除）。
/// 新版方式下 mods 只有一个频道：wire 名保留历史的 <c>std</c> 拼写（与已发布的工具互通），
/// 历史的 <c>pfm</c> 双频道拆分已移除——一个包要么就是官方包，要么根本不可同步。</para>
public sealed class QlManifest
{
    /// <summary>本启动器支持的 schema 版本，必须一致。</summary>
    public const int SchemaVersion = 1;

    /// <summary>mods 频道的 wire 名：保留历史拼写 <c>std</c>，但不再有第二个 mods 频道。</summary>
    public const string ChannelMods = "std";

    /// <summary>拥有独立 mods 文件夹的频道集合；新版只有一个 <see cref="ChannelMods"/>。</summary>
    public static readonly string[] ModsChannels = { ChannelMods };

    /// <summary>共享频道名：文件直接相对游戏目录解析。</summary>
    public const string ChannelCommon = "common";

    /// <summary>游戏内 RPU mod 独占的目录：启动器从不在这里下载、追踪或删除文件。</summary>
    /// <para><c>qlrpu</c> 是 RPU 客户端自己的仓库（cache/current.json + 加密 cache/objects/*.obj）。
    /// resourcepacks <b>不在</b>此列：整合包仍然附带若干资源包，照常由 mrpack 同步管理。</para>
    public static readonly string[] RpuFolders = { "qlrpu" };

    /// <summary>启动器自有目录里仍归 RPU 客户端所有的个别文件（它是自己设置的唯一写入者）。</summary>
    public static readonly string[] RpuFiles = { "config/qlrpu-client.json" };

    /// <summary>逻辑路径是否属于游戏内 RPU mod 而非启动器。</summary>
    public static bool IsRpuOwned(string? logicalPath)
    {
        if (logicalPath == null) return false;
        var normalized = logicalPath.Replace('\\', '/');
        if (RpuFiles.Contains(normalized, StringComparer.Ordinal)) return true;
        var slash = normalized.IndexOf('/');
        var segment = slash < 0 ? normalized : normalized[..slash];
        return RpuFolders.Contains(segment, StringComparer.Ordinal);
    }

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

    /// <summary>未知频道名归一成 common（历史的 pfm 也不再有独立频道，同样落 common）。</summary>
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
        foreach (var required in new[] { ChannelMods, ChannelCommon })
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
