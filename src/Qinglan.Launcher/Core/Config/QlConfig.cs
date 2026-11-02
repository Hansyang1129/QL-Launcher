namespace Qinglan.Launcher.Core;

/// <summary>启动器轻量配置（data/config.json）：只存跨会话需要的玩家偏好。</summary>
/// <para>容忍损坏——读不动给默认值，绝不能一个坏配置烧坏启动器（与同步状态同款纪律）。</para>
public sealed class QlConfig
{
    /// <summary>QL2 实例的游戏目录（含 mods/ 的根文件夹）。</summary>
    [JsonPropertyName("gameDir")] public string? GameDir { get; set; }

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>装载；文件缺失或损坏时给空配置。</summary>
    public static QlConfig Load(string configDir)
    {
        var file = Path.Combine(configDir, "config.json");
        try
        {
            if (!File.Exists(file)) return new QlConfig();
            return JsonSerializer.Deserialize<QlConfig>(File.ReadAllText(file)) ?? new QlConfig();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Globals.Log("配置读取失败(忽略): " + e.Message);
            return new QlConfig();
        }
    }

    /// <summary>保存；IO 失败抛出，由调用方决定如何提示。</summary>
    public void Save(string configDir)
    {
        Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "config.json"),
            JsonSerializer.Serialize(this, JsonOpts), new UTF8Encoding(false));
    }
}
