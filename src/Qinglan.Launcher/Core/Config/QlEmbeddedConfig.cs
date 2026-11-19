using System.Reflection;

namespace Qinglan.Launcher.Core;

/// <summary>
/// 编译期嵌入的默认配置（仅取 <c>deployment</c> 段与 <c>microsoftClientId</c>）。
/// <para>官方/本服构建把 <c>src/Qinglan.Launcher/config.embedded.json</c>（已 gitignore）
/// 作为 EmbeddedResource 打进 exe，用户无需再放 <c>data/config/config.json</c>。
/// 开源仓库不提交该文件；文件不存在时自动退化为全空配置。</para>
/// <para>外部配置的 <c>deployment</c> 段若存在，则整段覆盖内嵌值（留空 = 禁用模块的语义不变）。</para>
/// </summary>
public static class QlEmbeddedConfig
{
    private const string ResourceName = "Qinglan.Launcher.config.embedded.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly Lazy<QlConfig?> Defaults = new(LoadCore);

    /// <summary>内嵌的部署端点；没有内嵌文件时为 null。</summary>
    public static QlDeploymentConfig? Deployment => Defaults.Value?.Deployment;

    /// <summary>内嵌的微软 OAuth client id；没有时为空。</summary>
    public static string? MicrosoftClientId => Defaults.Value?.MicrosoftClientId;

    private static QlConfig? LoadCore()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
            if (stream is null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = JsonDocument.Parse(reader.ReadToEnd());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // 兼容两种写法：完整 config（含 deployment 段）或裸 deployment 对象。
            if (root.TryGetProperty("deployment", out var deployment))
            {
                if (deployment.ValueKind != JsonValueKind.Object) return null;
                return root.Deserialize<QlConfig>(JsonOpts) ?? new QlConfig();
            }
            return new QlConfig { Deployment = root.Deserialize<QlDeploymentConfig>(JsonOpts) };
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Globals.Log("内嵌配置读取失败(忽略): " + e.Message);
            return null;
        }
    }
}
