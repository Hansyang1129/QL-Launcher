using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Qinglan.Tests;

/// <summary>
/// 测试用 HTTP 文件服务器（TcpListener 自实现，免 URLACL）。
/// 借鉴 QL-HMCL 行为备忘 §7-10 的教训：线程须 daemon、Stop 必须 finally、payload 走流式。
/// M2b 扩展：请求唇言/头部记录 + 序列应答器 + 命中计数（OAuth/JRE 流程测试）。
/// </summary>
public sealed class MiniHttpServer : IAsyncDisposable, IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, byte[]> _files;   // 小写路径键
    private readonly Dictionary<string, int> _status;     // 路径 -> 自定义状态码（缺省 200）
    private readonly HashSet<string> _delayed;            // 被点到的路径延迟 1.5s（取消场景）
    private readonly Dictionary<string, Func<int, string, (int Status, byte[] Body)>> _responders;
    private readonly Dictionary<string, int> _callIndex;  // 应答器调用序号（1 起）
    private readonly Dictionary<string, List<string>> _bodies;   // 每路径收到的请求唇言
    private readonly Dictionary<string, List<string>> _headers;  // 每路径收到的请求头块（首个在前）
    private readonly Dictionary<string, int> _hits;       // 每路径总命中次数（GET/POST alike）
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
        _responders = new(StringComparer.OrdinalIgnoreCase);
        _callIndex = new(StringComparer.OrdinalIgnoreCase);
        _bodies = new(StringComparer.OrdinalIgnoreCase);
        _headers = new(StringComparer.OrdinalIgnoreCase);
        _hits = new(StringComparer.OrdinalIgnoreCase);
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

    /// <summary>序列应答器：第 n 次请求返回第 n 个应答；比 n 多则停在最后一个。</summary>
    public void AddJsonSequence(string urlPath, params string[] jsonSequence)
    {
        _responders[Normalize(urlPath)] = (call, _) =>
        {
            var json = jsonSequence[Math.Min(call, jsonSequence.Length) - 1];
            return (call <= jsonSequence.Length ? 200 : 200, Encoding.UTF8.GetBytes(json));
        };
    }

    /// <summary>状态可指定 + 固定负载应答器（变状态脚本化用）。</summary>
    public void AddResponder(string urlPath, Func<int, string, (int, byte[])> responder)
        => _responders[Normalize(urlPath)] = responder;

    /// <summary>该路径累计被请求的次数（任何方法）。</summary>
    public int Hits(string urlPath) => _hits.GetValueOrDefault(Normalize(urlPath));

    /// <summary>该路径收到的请求唇言序列（按到达顺序）。</summary>
    public IReadOnlyList<string> Bodies(string urlPath)
        => _bodies.TryGetValue(Normalize(urlPath), out var list) ? list : Array.Empty<string>();

    /// <summary>该路径收到的请求头块序列（原始多行文本，用于断言 Authorization 等）。</summary>
    public IReadOnlyList<string> Headers(string urlPath)
        => _headers.TryGetValue(Normalize(urlPath), out var list) ? list : Array.Empty<string>();

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
            var req = await ReadRequestAsync(stream, ct);
            if (req == null) return;

            var path = req.Value.Path;
            int status;
            byte[] payload;

            if (_responders.TryGetValue(path, out var responder))
            {
                int call;
                lock (_callIndex)
                {
                    call = _callIndex.GetValueOrDefault(path) + 1;
                    _callIndex[path] = call;
                    _hits[path] = _hits.GetValueOrDefault(path) + 1;
                    (_bodies.TryGetValue(path, out var bl) ? bl : _bodies[path] = new()).Add(req.Value.BodyText);
                    (_headers.TryGetValue(path, out var hl) ? hl : _headers[path] = new()).Add(req.Value.HeaderBlock);
                }
                (status, payload) = responder(call, req.Value.BodyText);
                payload ??= Array.Empty<byte>();
            }
            else
            {
                lock (_callIndex) _hits[path] = _hits.GetValueOrDefault(path) + 1;
                if (_delayed.Contains(path))
                    await Task.Delay(1500, ct);

                status = _status.GetValueOrDefault(path, 200);
                payload = _files.GetValueOrDefault(path, Array.Empty<byte>());
                if (status == 200 && !_files.ContainsKey(path)) { status = 404; payload = Array.Empty<byte>(); }
            }

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

    private static async Task<(string Path, string HeaderBlock, string BodyText)?> ReadRequestAsync(NetworkStream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var sb = new StringBuilder();
        while (true)
        {
            int n = await s.ReadAsync(buf, ct);
            if (n <= 0) return null;
            sb.Append(Encoding.UTF8.GetString(buf, 0, n));
            var text = sb.ToString();
            var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (headEnd < 0)
            {
                if (text.Length > 65536) return null;
                continue;
            }
            var head = text[..headEnd];
            var lines = head.Split("\r\n");
            if (lines.Length == 0) return null;
            var reqLine = lines[0];
            var sp = reqLine.Split(' ');
            if (sp.Length < 2) return null;
            var path = new Uri("http://x" + sp[1]).AbsolutePath;

            var rest = text[(headEnd + 4)..]; // 头块之后可能已带入的部分唇言
            int contentLength = 0;
            foreach (var h in lines.Skip(1))
            {
                var idx = h.IndexOf(':');
                if (idx > 0 && h[..idx].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(h[(idx + 1)..].Trim(), out contentLength);
            }
            contentLength = Math.Min(contentLength, 4 * 1024 * 1024);
            while (Encoding.UTF8.GetByteCount(rest) < contentLength)
            {
                n = await s.ReadAsync(buf, ct);
                if (n <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                rest = sb.ToString()[(sb.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
            }
            if (contentLength > 0 && rest.Length > contentLength) rest = rest[..contentLength];
            return (path, head, rest);
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
