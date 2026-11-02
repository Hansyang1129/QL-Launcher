using System.Net;
using System.Net.Http;
using System.Text;

namespace Qinglan.Launcher.Core.Net;

/// <summary>进程级 HTTP 客户端 + 带重试的文件下载。</summary>
public static class QlHttp
{
    /// <summary>共享 HttpClient（DNS 缓存友好；gzip 自动解压）。</summary>
    public static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", "Qinglan Launcher/" + Globals.Version } },
    };

    /// <summary>GET 文本，带 3 次重试（间隔 600ms 渐加）。</summary>
    public static async Task<string> GetTextAsync(string url, CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();
                return Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false));
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw; // 用户取消：不重试
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                if (attempt >= 3) throw;
                Globals.Log("GET 失败(第 " + attempt + " 次): " + url + " -> " + e.Message);
                await Task.Delay(600 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>校验下载 URL；非法（非绝对 http/https）时返回 false。</summary>
    public static bool IsValidHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    /// <summary>按候选顺序把文件拉到 target；每次尝试写 .part 再校验 SHA-1，匹配才落位。</summary>
    /// <returns>true=成功；false=所有候选都失败。</returns>
    public static async Task<bool> DownloadToFileAsync(IReadOnlyList<string> urls, string target,
        string expectedSha1, CancellationToken ct = default)
    {
        var fi = new FileInfo(target);
        var part = target + ".part";
        // 父目录先建齐（含 .part 书写处）：HMCL 的 FileDownloadTask 同样先备好目录再落盘。
        Directory.CreateDirectory(fi.DirectoryName ?? ".");
        for (int u = 0; u < urls.Count; u++)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (File.Exists(part)) File.Delete(part);
                    using (var resp = await Client.GetAsync(urls[u], HttpCompletionOption.ResponseHeadersRead, ct)
                               .ConfigureAwait(false))
                    {
                        resp.EnsureSuccessStatusCode();
                        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                        await using var dst = File.Create(part);
                        await src.CopyToAsync(dst, 256 * 1024, ct).ConfigureAwait(false);
                    }
                    if (Sha1Hex(part) != expectedSha1)
                        throw new IOException("下载内容 SHA-1 不符: " + target);
                    File.Move(part, target, overwrite: true);
                    return true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception e) when (e is HttpRequestException or IOException)
                {
                    Globals.Log("下载 " + target + " <- " + urls[u] + " 失败(" + (attempt + 1) + "/2): " + e.Message);
                    try { File.Delete(part); } catch { /* part 已不在 */ }
                    if (attempt == 1) break; // 换下一个候选 URL
                }
            }
        }
        return false;
    }

    /// <summary>文件 SHA-1 小写十六进制（流式读取，免读全文件进内存）。</summary>
    public static string Sha1Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }
}
