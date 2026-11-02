using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Qinglan.Tests;

/// <summary>
/// 测试用 HTTP 文件服务器（TcpListener 自实现，免 URLACL）。
/// 借鉴 QL-HMCL 行为备忘 §7-10 的教训：线程须 daemon、Stop 必须 finally、payload 走流式。
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable, IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, byte[]> _files;   // 小写路径键
    private readonly Dictionary<string, int> _status;     // 路径 -> 自定义状态码（缺省 200）
    private readonly HashSet<string> _delayed;            // 被点到的路径延迟 1.5s（取消场景）
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly int _port;

    private MiniHttpServer(int port, string baseDir)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _port = port;
        _files = new(StringComparer.OrdinalIgnoreCase);
        _status = new(StringComparer.OrdinalIgnoreCase);
        _delayed = new(StringComparer.OrdinalIgnoreCase);
        BaseDir = baseDir;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public string BaseDir { get; }

    /// <summary>端口随机起一个空闲口。</summary>
    public static async Task<MiniHttpServer> StartAsync(string baseDir)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var server = new MiniHttpServer(port, baseDir);
        await Task.Delay(60);
        return server;
    }

    public string BaseUrl => "http://127.0.0.1:" + _port + "/";

    public void AddFile(string urlPath, byte[] content)
    {
        _files[Normalize(urlPath)] = content;
    }

    public void FailPath(string urlPath, int status = 404) => _status[Normalize(urlPath)] = status;
    public void RemoveFail(string urlPath) => _status.Remove(Normalize(urlPath));
    public void DelayPath(string urlPath) => _delayed.Add(Normalize(urlPath));

    private static string Normalize(string path)
        => path.StartsWith('/') ? path.ToLowerInvariant() : "/" + path.ToLowerInvariant();

    private async Task AcceptLoopAsync()
    {
        try
        {
            _listener.Start();
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = Task.Run(() => HandleAsync(client, _cts.Token));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            Console.WriteLine("MiniHttp 停机: " + ex.Message);
        }
        finally
        {
            _listener.Stop(); // 教训条款：finally 关闭，否则 JVM/进程挂死
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var stream = client.GetStream();
            var req = await ReadRequestLineAsync(stream, ct);
            if (req == null) return;

            var path = req.Value.Path;
            if (_delayed.Contains(path))
                await Task.Delay(1500, ct);

            var status = _status.GetValueOrDefault(path, 200);
            var payload = _files.GetValueOrDefault(path, Array.Empty<byte>());
            if (status == 200 && !_files.ContainsKey(path)) { status = 404; payload = Array.Empty<byte>(); }

            var head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n" +
                       "Content-Length: " + payload.Length + "\r\n" +
                       "Connection: close\r\n" +
                       "\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            await stream.WriteAsync(headBytes, ct);
            // 流式 payload：不用 read-all（QL-HMCL 行为备忘：大文件 readAllBytes 会页面抖动）
            await stream.WriteAsync(payload, ct);
            await stream.FlushAsync(ct);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (Exception)
        {
            // 单句柄失败必须隔离，不许把 accept loop 拖死
        }
        finally
        {
            client.Close();
        }
    }

    private static async Task<(string Path, string RawLine)?> ReadRequestLineAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[4096];
        var sb = new StringBuilder();
        while (true)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n <= 0) return null;
            sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            var idx = sb.ToString().IndexOf("\r\n", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var line = sb.ToString(0, idx);
                var sp = line.Split(' ');
                if (sp.Length >= 2)
                {
                    var p = new Uri("http://x" + sp[1]).AbsolutePath;
                    return (p, line);
                }
                return null;
            }
            if (sb.Length > 32768) return null;
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        404 => "Not Found",
        500 => "Internal Server Error",
        _ => "Status",
    };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _acceptLoop.ConfigureAwait(false); } catch { /* 停机情绪不外漏 */ }
        _cts.Dispose();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _cts.Dispose();
    }
}
