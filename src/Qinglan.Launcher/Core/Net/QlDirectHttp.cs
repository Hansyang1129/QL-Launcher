using System.Net;
using System.Text;

namespace Qinglan.Launcher.Core.Net;

/// <summary>
/// 直连 HTTP 客户端：MTR 地图 API 与论坛都走这个客户端，强制绕过系统代理。
/// <para>用户常开 Watt Toolkit 等加速器，它会把非标端口请求改写（返回 404）或直接拒绝；
/// 这些公网服务本身可直连，因此与状态页探测保持同一口径。</para>
/// </summary>
public static class QlDirectHttp
{
    /// <summary>共享客户端；单请求超时 20s，连接超时 10s。</summary>
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Qinglan Launcher/" + Globals.Version } },
    };

    /// <summary>GET 文本，3 次重试（间隔 600ms 递增）。取消不重试。</summary>
    public static async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await Client.GetAsync(url, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= 3) throw new IOException("请求超时：" + url);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                if (attempt >= 3) throw;
            }
            await Task.Delay(600 * attempt, ct).ConfigureAwait(false);
        }
    }

    /// <summary>POST JSON 文本，3 次重试（间隔 600ms 递增）。取消不重试。</summary>
    public static async Task<string> PostJsonAsync(string url, string json, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await Client.PostAsync(url, content, ct).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= 3) throw new IOException("请求超时：" + url);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                if (attempt >= 3) throw;
            }
            await Task.Delay(600 * attempt, ct).ConfigureAwait(false);
        }
    }
}
