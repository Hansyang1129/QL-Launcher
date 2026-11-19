namespace Qinglan.Launcher.Core;

/// <summary>启动器轻量配置（data/config/config.json）：只存跨会话需要的玩家偏好。</summary>
/// <para>容忍损坏——读不动给默认值，绝不能一个坏配置烧坏启动器（与同步状态同款纪律）。</para>
public sealed class QlConfig
{
    /// <summary>游戏根目录（.minecraft 或等价的根文件夹）。</summary>
    /// <para>受管实例固定是根目录下的 <c>versions\Qinglan-2nd-RPU</c>（版本隔离）；
    /// 根目录自身就是实例运行目录（含 mods/ 或 QL 状态标记）的平铺布局也兼容。
    /// 解析规则见 <see cref="QlInstances.ResolveRunDir"/>。</para>
    [JsonPropertyName("gameDir")] public string? GameDir { get; set; }

    /// <summary>手工指定的 Java（bin 上级目录）；空 = 自动探测。</summary>
    [JsonPropertyName("javaDir")] public string? JavaDir { get; set; }

    /// <summary>微软登录 refresh_token（静默续期用）；空 = 没登录过微软账号。</summary>
    [JsonPropertyName("microsoftRefreshToken")] public string? MicrosoftRefreshToken { get; set; }

    /// <summary>微软 OAuth 应用 client id；空 = 禁用微软登录（离线启动照常）。</summary>
    [JsonPropertyName("microsoftClientId")] public string? MicrosoftClientId { get; set; }

    /// <summary>最近一次微软登录的玩家名（仅界面展示；启动前仍会刷新令牌）。</summary>
    [JsonPropertyName("microsoftAccountName")] public string? MicrosoftAccountName { get; set; }

    /// <summary>离线玩家名（未登录微软时使用）。</summary>
    [JsonPropertyName("playerName")] public string? PlayerName { get; set; }

    /// <summary>游戏内存上限 MB；0 = 自动（按物理内存取 1/4，夹 [2048, 8192]）。</summary>
    [JsonPropertyName("maxMemoryMb")] public int MaxMemoryMb { get; set; }

    /// <summary>部署端点（A 类留空 = 模块禁用；B 类留空 = 官方默认）。</summary>
    /// <para>开源仓库只提交空壳模板；真实值由本地 <c>data/config/config.json</c> 提供。
    /// 字段语义与归一化见 <see cref="QlDeployment"/>。</para>
    [JsonPropertyName("deployment")] public QlDeploymentConfig? Deployment { get; set; }

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
