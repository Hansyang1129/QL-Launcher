using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core;

/// <summary>从资源服务器拉 manifest 并严格校验。</summary>
public static class QlManifestFetcher
{
    /// <summary>拉取 + 解析 manifest。</summary>
    /// <exception cref="ManifestException">内容不符合 manifest v1。</exception>
    public static async Task<QlManifest> FetchAsync(string url, CancellationToken ct = default)
    {
        var text = await QlHttp.GetTextAsync(url, ct).ConfigureAwait(false);
        return QlManifest.FromJson(text);
    }

    /// <summary>从本地文件装载 manifest（群文件兜底场景用）。</summary>
    public static QlManifest LoadFromFile(string path)
        => QlManifest.FromJson(File.ReadAllText(path));
}
