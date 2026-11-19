using Qinglan.Launcher.Core.Auth;

namespace Qinglan.Launcher.Core;

/// <summary>
/// 部署端点配置（<c>data/config/config.json</c> 的 <c>deployment</c> 段）。
/// <para>A 类字段留空 = 对应模块禁用或降级；B 类（微软官方端点）留空 = 回落官方默认。
/// 开源仓库只提交空壳模板，真实部署值由本地配置或构建期内嵌文件提供，不进版本库。</para>
/// </summary>
public sealed class QlDeploymentConfig
{
    /// <summary>Minecraft 服务器（SLP 探测 + 生成 manifest 的 servers 段）。</summary>
    [JsonPropertyName("server")] public QlServerDeployment Server { get; set; } = new();

    /// <summary>MTR 地图 API 根地址；同时承载 MTR 页、地图页与状态探测。</summary>
    [JsonPropertyName("mtr")] public QlEndpointDeployment Mtr { get; set; } = new();

    /// <summary>Reporter 服务根地址（状态探测拼 <c>/login</c>）。</summary>
    [JsonPropertyName("reporter")] public QlEndpointDeployment Reporter { get; set; } = new();

    /// <summary>论坛根地址（Halo API、RSS 与文章外链）。</summary>
    [JsonPropertyName("forum")] public QlEndpointDeployment Forum { get; set; } = new();

    /// <summary>同步相关部署端点。</summary>
    [JsonPropertyName("sync")] public QlSyncDeployment Sync { get; set; } = new();

    /// <summary>微软登录端点；留空时逐项回落到官方默认。</summary>
    [JsonPropertyName("auth")] public QlAuthDeployment Auth { get; set; } = new();

    /// <summary>日志上报占位（当前版本没有上传实现）。</summary>
    [JsonPropertyName("logReport")] public QlLogReportDeployment LogReport { get; set; } = new();

    /// <summary>把 JSON 里显式 null 的子段补回对象，并修剪地址空白与末尾斜杠。</summary>
    public QlDeploymentConfig Normalize()
    {
        Server ??= new QlServerDeployment();
        Mtr ??= new QlEndpointDeployment();
        Reporter ??= new QlEndpointDeployment();
        Forum ??= new QlEndpointDeployment();
        Sync ??= new QlSyncDeployment();
        Auth ??= new QlAuthDeployment();
        LogReport ??= new QlLogReportDeployment();

        Server.Name = (Server.Name ?? "").Trim();
        Server.SlpHost = (Server.SlpHost ?? "").Trim();
        if (Server.SlpPort is < 0 or > 65535) Server.SlpPort = 0;

        Mtr.BaseUrl = TrimBase(Mtr.BaseUrl);
        Reporter.BaseUrl = TrimBase(Reporter.BaseUrl);
        Forum.BaseUrl = TrimBase(Forum.BaseUrl);
        Sync.PublisherBaseUrl = TrimBase(Sync.PublisherBaseUrl);

        Auth.DeviceCodeUrl = (Auth.DeviceCodeUrl ?? "").Trim();
        Auth.TokenUrl = (Auth.TokenUrl ?? "").Trim();
        Auth.XblAuthUrl = (Auth.XblAuthUrl ?? "").Trim();
        Auth.XstsAuthUrl = (Auth.XstsAuthUrl ?? "").Trim();
        Auth.McLoginUrl = (Auth.McLoginUrl ?? "").Trim();
        Auth.McProfileUrl = (Auth.McProfileUrl ?? "").Trim();
        LogReport.Url = (LogReport.Url ?? "").Trim();
        return this;
    }

    private static string TrimBase(string? value)
        => (value ?? "").Trim().TrimEnd('/');
}

/// <summary>Minecraft 服务器段。</summary>
public sealed class QlServerDeployment
{
    /// <summary>界面显示名；空 = 用 host:port 兜底。</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>SLP 主机名；空 = 状态页不生成服务器探测项。</summary>
    [JsonPropertyName("slpHost")] public string SlpHost { get; set; } = "";

    /// <summary>SLP 端口；0 = 未配置。</summary>
    [JsonPropertyName("slpPort")] public int SlpPort { get; set; }
}

/// <summary>只有一个根地址的服务段。</summary>
public sealed class QlEndpointDeployment
{
    /// <summary>根地址（http/https）；空 = 未配置。</summary>
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; set; } = "";
}

/// <summary>同步段：发布器兜底资源地址。</summary>
public sealed class QlSyncDeployment
{
    /// <summary>清单条目的兜底下载前缀；空 = 不追加兜底候选（只用包内索引地址）。</summary>
    [JsonPropertyName("publisherBaseUrl")] public string PublisherBaseUrl { get; set; } = "";
}

/// <summary>微软/Xbox/Minecraft 登录端点（B 类：留空回落官方默认）。</summary>
public sealed class QlAuthDeployment
{
    [JsonPropertyName("deviceCodeUrl")] public string DeviceCodeUrl { get; set; } = "";
    [JsonPropertyName("tokenUrl")] public string TokenUrl { get; set; } = "";
    [JsonPropertyName("xblAuthUrl")] public string XblAuthUrl { get; set; } = "";
    [JsonPropertyName("xstsAuthUrl")] public string XstsAuthUrl { get; set; } = "";
    [JsonPropertyName("mcLoginUrl")] public string McLoginUrl { get; set; } = "";
    [JsonPropertyName("mcProfileUrl")] public string McProfileUrl { get; set; } = "";
}

/// <summary>日志上报占位段；当前仓库没有上传实现。</summary>
public sealed class QlLogReportDeployment
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
}

/// <summary>
/// 当前进程使用的部署配置（启动时由 <see cref="Load"/> 装载一次）。
/// <para>空字段统一在这里判定：各页面/引擎只查 <c>HasXxx</c>，不要各自解析 URL。
/// 地址必须是非空 http/https；A 类为空时对应模块显示统一占位，不发请求。</para>
/// </summary>
public static class QlDeployment
{
    private static QlDeploymentConfig _current = new();

    /// <summary>当前部署配置（永远非 null；空配置返回全空对象）。</summary>
    public static QlDeploymentConfig Current => _current;

    /// <summary>启动时装载：外部 deployment 段优先；没有则用内嵌兜底；都没有按全空处理。</summary>
    public static void Load(QlConfig config, QlDeploymentConfig? embedded = null)
        => Set(config.Deployment ?? embedded ?? new QlDeploymentConfig());

    /// <summary>覆盖当前配置（测试/热重载用）；自动 Normalize。</summary>
    public static void Set(QlDeploymentConfig deployment) => _current = deployment.Normalize();

    /// <summary>恢复全空配置（测试清理用）。</summary>
    public static void Reset() => _current = new QlDeploymentConfig();

    // ─────────────────────────── 模块判定 ───────────────────────────

    /// <summary>服务器 SLP 是否就绪。</summary>
    public static bool HasServer
        => !string.IsNullOrWhiteSpace(Current.Server.SlpHost)
           && Current.Server.SlpPort is > 0 and <= 65535;

    /// <summary>服务器显示名；未填时用 Minecraft 兜底。</summary>
    public static string ServerName
        => string.IsNullOrWhiteSpace(Current.Server.Name) ? "Minecraft" : Current.Server.Name;

    /// <summary>MTR 服务是否就绪。</summary>
    public static bool HasMtr => IsHttpUrl(Current.Mtr.BaseUrl);

    /// <summary>MTR 根地址（已去末尾斜杠）；未配置时为空串。</summary>
    public static string MtrBaseUrl => HasMtr ? Current.Mtr.BaseUrl : "";

    /// <summary>MTR 网页地图入口（根地址 + 斜杠）；未配置时为空串。</summary>
    public static string MtrWebUrl => HasMtr ? Current.Mtr.BaseUrl + "/" : "";

    /// <summary>Reporter 服务是否就绪。</summary>
    public static bool HasReporter => IsHttpUrl(Current.Reporter.BaseUrl);

    /// <summary>Reporter 根地址；未配置时为空串。</summary>
    public static string ReporterBaseUrl => HasReporter ? Current.Reporter.BaseUrl : "";

    /// <summary>论坛是否就绪。</summary>
    public static bool HasForum => IsHttpUrl(Current.Forum.BaseUrl);

    /// <summary>论坛根地址；未配置时为空串。</summary>
    public static string ForumBaseUrl => HasForum ? Current.Forum.BaseUrl : "";

    /// <summary>发布器兜底下载前缀是否就绪。</summary>
    public static bool HasPublisher => IsHttpUrl(Current.Sync.PublisherBaseUrl);

    /// <summary>发布器兜底下载前缀（已去末尾斜杠）；未配置时为空串。</summary>
    public static string PublisherBaseUrl
        => HasPublisher ? Current.Sync.PublisherBaseUrl : "";

    /// <summary>微软登录端点：逐项回落官方默认，保证 config 为空也能登录。</summary>
    public static QlMsEndpoints AuthEndpoints
    {
        get
        {
            var auth = Current.Auth;
            var real = QlMsEndpoints.Real;
            return new QlMsEndpoints(
                OrDefault(auth.DeviceCodeUrl, real.DeviceCodeUrl),
                OrDefault(auth.TokenUrl, real.TokenUrl),
                OrDefault(auth.XblAuthUrl, real.XblAuthUrl),
                OrDefault(auth.XstsAuthUrl, real.XstsAuthUrl),
                OrDefault(auth.McLoginUrl, real.McLoginUrl),
                OrDefault(auth.McProfileUrl, real.McProfileUrl));
        }
    }

    // ─────────────────────────── 调用辅助 ───────────────────────────

    /// <summary>取 MTR 根地址；未配置时抛出（页面应先判 <see cref="HasMtr"/>）。</summary>
    public static string RequireMtrBaseUrl()
        => HasMtr ? MtrBaseUrl : throw new InvalidOperationException(MissingHint("MTR 服务", "deployment.mtr.baseUrl"));

    /// <summary>取论坛根地址；未配置时抛出（页面应先判 <see cref="HasForum"/>）。</summary>
    public static string RequireForumBaseUrl()
        => HasForum ? ForumBaseUrl : throw new InvalidOperationException(MissingHint("论坛", "deployment.forum.baseUrl"));

    /// <summary>帖子绝对地址；论坛未配置时保留相对地址，避免拼出假 URL。</summary>
    public static string ForumUrl(string permalink)
        => HasForum ? ForumBaseUrl + permalink : permalink;

    /// <summary>统一的“配置未就绪”文案（页面/状态栏共用）。</summary>
    public static string MissingHint(string module, string field)
        => module + "未配置：请在本机 data/config/config.json 的 " + field
           + " 填写部署地址后重启启动器（字段说明见 config.example.json）。";

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string OrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
