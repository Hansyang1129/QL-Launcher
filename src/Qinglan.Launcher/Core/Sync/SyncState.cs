using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Qinglan.Launcher.Core;

/// <summary>轻蓝实例的持久化用户状态，文件名 ql-manifest-state.json。</summary>
/// <para>两个职责：一是记住上一份 manifest 装过的文件，让从新 manifest 里消失的文件能被
/// 安全清理、且永不碰用户手放的内容；二是保存频道与可选组等用户选择。</para>
public sealed class QlSyncState
{
    /// <summary>游戏目录内的状态文件名。</summary>
    public const string StateFile = "ql-manifest-state.json";

    /// <summary>上次成功应用的 manifest 的整合包版本。</summary>
    [JsonPropertyName("packVersion")] public string? PackVersion { get; set; }

    /// <summary>用户认可/输入的 manifest 地址；空白回落默认地址。</summary>
    [JsonPropertyName("manifestUrl")] public string? ManifestUrl { get; set; }

    /// <summary>当前活跃频道：std 或 pfm。</summary>
    [JsonPropertyName("activeChannel")] public string ActiveChannel { get; set; } = "std";

    /// <summary>用户启用的可选组。</summary>
    [JsonPropertyName("enabledOptional")] public List<string> EnabledOptional { get; set; } = new();

    /// <summary>上次安装的文件：频道名 -> 逻辑路径 -> sha1。</summary>
    [JsonPropertyName("tracked")]
    public Dictionary<string, Dictionary<string, string>> Tracked { get; set; } = new();

    /// <summary>全新空状态。</summary>
    public static QlSyncState Empty() => new();

    /// <summary>整表替换 tracked；顺带丢弃空频道映射。</summary>
    public void ReplaceTracked(IEnumerable<KeyValuePair<string, Dictionary<string, string>>> newTracked)
    {
        Tracked.Clear();
        foreach (var (channel, map) in newTracked)
            if (map is { Count: > 0 })
                Tracked[channel] = new Dictionary<string, string>(map, StringComparer.Ordinal);
    }

    /// <summary>某频道 tracked 的只读视图（无则空表）。</summary>
    public IReadOnlyDictionary<string, string> TrackedOf(string channel)
        => Tracked.TryGetValue(channel, out var m) ? m : EmptyMap;

    private static readonly Dictionary<string, string> EmptyMap = new();

    /// <summary>登记一个已对账文件的摘要。</summary>
    public void Track(string channel, string logicalPath, string sha1)
    {
        if (!Tracked.TryGetValue(channel, out var map))
            Tracked[channel] = map = new Dictionary<string, string>(StringComparer.Ordinal);
        map[logicalPath] = sha1;
    }

    /// <summary>登记/覆盖一批：</summary>
    public void Track(string channel, Dictionary<string, string> pathsToSha)
    {
        if (!Tracked.TryGetValue(channel, out var map))
            Tracked[channel] = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, s) in pathsToSha)
            Tracked[channel][p] = s;
    }

    /// <summary>文件不再被管理时解除登记。</summary>
    public void Untrack(string channel, string logicalPath)
    {
        if (Tracked.TryGetValue(channel, out var m)) m.Remove(logicalPath);
    }

    /// <summary>可选组当前是否启用。</summary>
    public bool IsOptionalEnabled(string group) => EnabledOptional.Contains(group);

    /// <summary>启用集合的只读拷贝。</summary>
    public IReadOnlySet<string> EnabledOptionalGroups()
        => new HashSet<string>(EnabledOptional, StringComparer.Ordinal);

    /// <summary>用户标记可选组开/关。</summary>
    public void SetOptionalEnabled(string group, bool enabled)
    {
        if (enabled) { if (!EnabledOptional.Contains(group)) EnabledOptional.Add(group); }
        else EnabledOptional.Remove(group);
    }

    /// <summary>装载游戏目录的状态；没有就给空状态。</summary>
    /// <para>容忍上次崩溃截断的文件：损坏内容挪成 .corrupt 备查，空态起步，
    /// 一个坏的 ql-manifest-state.json 不能烧坏之后每一次 sync。</para>
    public static QlSyncState Load(string gameDir)
    {
        var file = Path.Combine(gameDir, StateFile);
        if (!File.Exists(file)) return new QlSyncState();
        try
        {
            var state = JsonSerializer.Deserialize<QlSyncState>(File.ReadAllText(file), JsonOpts);
            if (state == null) return new QlSyncState();
            state.Tracked ??= new();
            state.EnabledOptional ??= new();
            if (string.IsNullOrWhiteSpace(state.ActiveChannel)) state.ActiveChannel = "std";
            return state;
        }
        catch (JsonException e)
        {
            // 破损挪 aside；挪不走就抛出去（不能假装成功还得覆盖写回）。
            try { File.Move(file, file + ".corrupt", overwrite: true); }
            catch (IOException moveFailure) { throw new IOException("状态文件损坏且无法挪 aside", moveFailure); }
            Globals.Log("损坏的同步状态已挪 aside: " + file + " (" + e.Message + ")");
            return new QlSyncState();
        }
    }

    /// <summary>原子保存：先写 .tmp 再改名进位，途中崩溃不会留截断的状态文件。</summary>
    public void Save(string gameDir)
    {
        var file = Path.Combine(gameDir, StateFile);
        var tmp = file + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts), Utf8NoBom);
            File.Move(tmp, file, overwrite: true);
        }
        catch (IOException)
        {
            try { File.Delete(tmp); } catch { /* 删不掉也抛原始错误 */ }
            throw;
        }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    internal static string ToJsonString(QlSyncState s) => JsonSerializer.Serialize(s, JsonOpts);
}
