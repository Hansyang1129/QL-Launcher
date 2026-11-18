using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Qinglan.Launcher.Core.Status;

/// <summary>探测方式：Minecraft 服务器列表查询（原始 TCP）或普通 HTTP GET。</summary>
public enum QlProbeKind
{
    /// <summary>Minecraft Java Server List Ping（1.7+ varint 分帧）。</summary>
    Slp,
    /// <summary>普通 HTTP/HTTPS GET；仅 200 视为在线。</summary>
    Http,
}

/// <summary>一个被监控的端点。
/// <para>SLP 用 <paramref name="Host"/>/<paramref name="Port"/>；HTTP 用 <paramref name="Url"/>，
/// 另外两个字段仅作展示信息。与 QL-HMCL 的 <c>QLServerStatus.Probe</c> 同构。</para></summary>
/// <param name="Label">界面显示名；空 = 用 host:port / URL 兜底。</param>
/// <param name="Kind">探测方式。</param>
/// <param name="Host">SLP 主机名；HTTP 时为信息字段。</param>
/// <param name="Port">SLP 端口；HTTP 时为 0。</param>
/// <param name="Url">HTTP 完整地址；SLP 时为 null。</param>
public sealed record QlProbe(string? Label, QlProbeKind Kind, string Host, int Port, string? Url)
{
    /// <summary>构造一个 Minecraft SLP 探测。</summary>
    public static QlProbe Slp(string? label, string host, int port)
        => new(label, QlProbeKind.Slp, host, port, null);

    /// <summary>构造一个 HTTP 探测。</summary>
    public static QlProbe Http(string? label, string host, int port, string url)
        => new(label, QlProbeKind.Http, host, port, url);

    /// <summary>界面标题：label 优先，空时回落到地址。</summary>
    public string DisplayLabel => !string.IsNullOrWhiteSpace(Label)
        ? Label!
        : Kind == QlProbeKind.Slp ? Host + ":" + Port : Url ?? "";

    /// <summary>端点地址（SLP 为 host:port，HTTP 为 URL）。</summary>
    public string Address => Kind == QlProbeKind.Slp ? Host + ":" + Port : Url ?? "";
}

/// <summary>单个端点的探测结果。与 QL-HMCL 的 <c>ProbeResult</c> 同构。</summary>
/// <param name="Online">是否在超时内给出了有效应答。</param>
/// <param name="LatencyMs">从连接开始到收到有效应答的毫秒数；离线为 -1。</param>
/// <param name="Players">SLP 在线人数；HTTP 为 null。</param>
/// <param name="PlayersMax">SLP 槽位上限；HTTP 为 null。</param>
/// <param name="Detail">SLP 的 MOTD；HTTP 的 "HTTP nnn"；离线时是失败原因。</param>
/// <param name="Sample">SLP 当前在线玩家名；HTTP 为空表。</param>
public sealed record QlProbeResult(bool Online, long LatencyMs, int? Players, int? PlayersMax,
    string? Detail, IReadOnlyList<string> Sample);

/// <summary>一次成功 SLP 查询的原始数据。</summary>
/// <param name="LatencyMs">连接开始到响应收完的毫秒数。</param>
/// <param name="PlayersOnline">在线人数。</param>
/// <param name="PlayersMax">槽位上限。</param>
/// <param name="Motd">摊平后的 MOTD 纯文本（保留换行）。</param>
/// <param name="Sample">在线玩家名。</param>
public sealed record QlSlpResult(long LatencyMs, int PlayersOnline, int PlayersMax, string Motd,
    IReadOnlyList<string> Sample)
{
    /// <summary>转成 UI 使用的通用结果。</summary>
    public QlProbeResult ToProbeResult()
        => new(true, LatencyMs, PlayersOnline, PlayersMax, Motd, Sample);
}

/// <summary>HTTP 探测的原始结果。</summary>
/// <param name="LatencyMs">请求开始到读完响应的毫秒数。</param>
/// <param name="StatusCode">HTTP 状态码。</param>
/// <param name="Body">响应体前 300 个字符（最多读 4 KB）。</param>
public sealed record QlHttpResult(long LatencyMs, int StatusCode, string Body)
{
    /// <summary>只有 200 算在线；维护页/错误页不能伪装成健康。</summary>
    public bool IsUp => StatusCode == 200;

    /// <summary>转成 UI 使用的通用结果（HTTP 不带人数）。</summary>
    public QlProbeResult ToProbeResult()
        => new(IsUp, LatencyMs, null, null, "HTTP " + StatusCode, Array.Empty<string>());
}

/// <summary>
/// 轻蓝服务器集群探测：Minecraft SLP（varint 握手 + 状态请求）与 HTTP GET，
/// 语义对齐 QL-HMCL 的 <c>QLServerStatus</c>（协议 47、顺序探测、单点失败不影响整表）。
/// 所有方法都异步且受超时约束；异常只在单点内部转成离线结果，批量探测不抛出。
/// </summary>
public static class QlServerStatus
{
    /// <summary>默认探测表：轻蓝 SLP + MTR 地图 API + Reporter 登录页。
    /// <para>QL-HMCL 原版探这三个服务的根路径；实测根路径分别是 404/404/401（服务本身在线），
    /// 因此这里改探真正返回 200 的健康路径，保持“仅 200 算在线”的判定不变。</para></summary>
    public static IReadOnlyList<QlProbe> DefaultProbes { get; } = new[]
    {
        QlProbe.Slp("轻蓝", "mc.example.com", 32650),
        QlProbe.Http("MTR Web", "mc.example.com", 14614,
            "http://mc.example.com:14614/mtr/api/map/stations-and-routes?dimension=minecraft:overworld"),
        QlProbe.Http("Reporter", "mc.example.com", 19451,
            "http://mc.example.com:19451/login"),
    };

    /// <summary>SLP 状态响应体的上限，防止恶意对端让我们无界分配。</summary>
    private const int MaxResponseBytes = 1 << 20;

    /// <summary>HTTP 响应体最多读取的字节数（界面只展示前 300 字符）。</summary>
    private const int MaxHttpBodyBytes = 4 * 1024;

    /// <summary>
    /// 状态探测专用 HTTP 客户端：强制直连，不走系统代理。
    /// <para>用户常开着 Watt Toolkit 等系统代理，代理对非标端口（14614/19451）会直接回 404；
    /// 状态页要测的是端点本身的健康度，和 SLP 的直连 TCP 保持同一口径。</para>
    /// </summary>
    private static readonly HttpClient ProbeClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan, // 单次探测超时由 linked CTS 控制
        DefaultRequestHeaders = { { "User-Agent", "Qinglan Launcher/" + Globals.Version } },
    };

    /// <summary>顺序探测全部端点；单点异常收敛为离线结果。</summary>
    /// <param name="probes">探测表。</param>
    /// <param name="timeoutMs">单点连接/读取超时。</param>
    /// <param name="ct">整体取消（用户离页/退出）；取消会向上抛出。</param>
    public static async Task<List<QlProbeResult>> ProbeAllAsync(IReadOnlyList<QlProbe> probes,
        int timeoutMs, CancellationToken ct = default)
    {
        var results = new List<QlProbeResult>(probes.Count);
        foreach (var probe in probes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ProbeAsync(probe, timeoutMs, ct).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                results.Add(new QlProbeResult(false, -1, null, null, MessageOf(e), Array.Empty<string>()));
            }
        }
        return results;
    }

    /// <summary>探测单个端点；失败抛出（批量入口负责收敛）。</summary>
    public static async Task<QlProbeResult> ProbeAsync(QlProbe probe, int timeoutMs,
        CancellationToken ct = default)
        => probe.Kind switch
        {
            QlProbeKind.Slp => (await PingSlpAsync(probe.Host, probe.Port, timeoutMs, ct)
                .ConfigureAwait(false)).ToProbeResult(),
            QlProbeKind.Http => (await ProbeHttpAsync(probe.Url ?? "", timeoutMs, ct)
                .ConfigureAwait(false)).ToProbeResult(),
            _ => throw new IOException("未知探测类型：" + probe.Kind),
        };

    /// <summary>对 host:port 执行 Minecraft Server List Ping（1.7+ 协议）。</summary>
    /// <param name="host">主机名或字面量 IP。</param>
    /// <param name="port">端口。</param>
    /// <param name="timeoutMs">连接与读取共用的超时。</param>
    /// <param name="ct">整体取消。</param>
    public static async Task<QlSlpResult> PingSlpAsync(string host, int port, int timeoutMs,
        CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        var token = linked.Token;
        var watch = Stopwatch.StartNew();

        using var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, token).ConfigureAwait(false);
            client.NoDelay = true;
            var stream = client.GetStream();

            // 握手载荷：packetId(0) + protocol(47) + host + port(大端 ushort) + nextState(1)。
            using var payload = new MemoryStream();
            WriteVarInt(payload, 0);
            WriteVarInt(payload, 47);
            WriteString(payload, host);
            payload.WriteByte((byte)(port >> 8));
            payload.WriteByte((byte)(port & 0xFF));
            WriteVarInt(payload, 1);
            var handshake = payload.ToArray();

            // 帧：长度 varint + 载荷；随后再来一个长度 1 的状态请求（packetId 0）。
            using var outBuf = new MemoryStream();
            WriteVarInt(outBuf, handshake.Length);
            outBuf.Write(handshake);
            WriteVarInt(outBuf, 1);
            outBuf.WriteByte(0);
            await stream.WriteAsync(outBuf.ToArray(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);

            int packetLength = await ReadVarIntAsync(stream, token).ConfigureAwait(false);
            if (packetLength <= 0 || packetLength > MaxResponseBytes + 10)
                throw new IOException("SLP 响应分帧异常：packetLength=" + packetLength);

            int packetId = await ReadVarIntAsync(stream, token).ConfigureAwait(false);
            if (packetId != 0)
                throw new IOException("SLP 响应包 id 异常：" + packetId);

            int stringLength = await ReadVarIntAsync(stream, token).ConfigureAwait(false);
            int bodyLength = packetLength - VarIntSize(packetId) - VarIntSize(stringLength);
            if (bodyLength < 0 || bodyLength > MaxResponseBytes || stringLength < 0 ||
                stringLength > bodyLength)
                throw new IOException("SLP 响应分帧异常：packetLength=" + packetLength +
                                      "，stringLength=" + stringLength);

            var body = new byte[bodyLength];
            await stream.ReadExactlyAsync(body, token).ConfigureAwait(false);
            var latency = watch.ElapsedMilliseconds;

            using var doc = JsonDocument.Parse(body.AsMemory(0, stringLength));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new IOException("SLP 状态响应不是 JSON 对象");
            if (!root.TryGetProperty("players", out var players) ||
                players.ValueKind != JsonValueKind.Object)
                throw new IOException("SLP 响应缺少 players 对象");

            int online = IntOrDefault(players, "online");
            int max = IntOrDefault(players, "max");
            string motd = root.TryGetProperty("description", out var desc)
                ? FlattenText(desc)
                : "";
            var sample = ReadSample(players);
            return new QlSlpResult(Math.Max(0, latency), online, max, motd, sample);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("连接超时（" + timeoutMs + " ms）");
        }
        catch (SocketException e)
        {
            throw new IOException(DescribeSocketError(e), e);
        }
    }

    /// <summary>对 URL 执行 HTTP GET；状态码与耗时都带回（非 200 不算在线）。</summary>
    public static async Task<QlHttpResult> ProbeHttpAsync(string url, int timeoutMs,
        CancellationToken ct = default)
    {
        Uri uri;
        try
        {
            uri = new Uri(url, UriKind.Absolute);
        }
        catch (Exception e) when (e is UriFormatException or ArgumentException)
        {
            throw new IOException("探测 URL 无效：" + url, e);
        }
        if (uri.Scheme is not ("http" or "https"))
            throw new IOException("只支持 HTTP(S) 探测地址：" + url);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(timeoutMs);
        var token = linked.Token;
        var watch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await ProbeClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            int status = (int)response.StatusCode;

            // 只读头部与少量正文：探测不需要整页，避免被大页面拖住。
            string body = "";
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token)
                    .ConfigureAwait(false);
                var buffer = new byte[MaxHttpBodyBytes];
                int total = 0;
                while (total < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(total), token).ConfigureAwait(false);
                    if (n <= 0) break;
                    total += n;
                }
                if (total > 0) body = Encoding.UTF8.GetString(buffer, 0, total);
            }
            catch (Exception e) when ((e is IOException or HttpRequestException) &&
                                      !ct.IsCancellationRequested)
            {
                // 状态码已经拿到：正文读失败不影响“端点是否在线”的判断。
                Globals.Log("状态页 HTTP 正文读取失败(忽略): " + e.Message);
            }

            var latency = watch.ElapsedMilliseconds;
            if (body.Length > 300) body = body[..300];
            return new QlHttpResult(Math.Max(0, latency), status, body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException("请求超时（" + timeoutMs + " ms）");
        }
        catch (HttpRequestException e)
        {
            throw new IOException(e.Message, e);
        }
    }

    /// <summary>把 Minecraft 聊天组件（MOTD）摊平成纯文本，递归 extra，保留换行。</summary>
    /// <param name="element">SLP 响应的 description 节点。</param>
    /// <returns>拼接后的文本；缺失给空串，永不返回 null。</returns>
    public static string FlattenText(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString() ?? "";
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                return element.ToString();
            case JsonValueKind.Object:
                var sb = new StringBuilder();
                if (element.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                    sb.Append(text.GetString());
                if (element.TryGetProperty("extra", out var extra) &&
                    extra.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in extra.EnumerateArray())
                        sb.Append(FlattenText(child));
                }
                return sb.ToString();
            default:
                return "";
        }
    }

    private static List<string> ReadSample(JsonElement players)
    {
        var names = new List<string>();
        if (!players.TryGetProperty("sample", out var sample) ||
            sample.ValueKind != JsonValueKind.Array)
            return names;
        foreach (var entry in sample.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String &&
                name.GetString() is { Length: > 0 } value)
                names.Add(value);
        }
        return names;
    }

    private static int IntOrDefault(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.TryGetInt32(out int parsed) ? parsed : 0;

    private static string MessageOf(Exception e)
    {
        var message = e.Message;
        return string.IsNullOrWhiteSpace(message) ? e.ToString() : message;
    }

    private static string DescribeSocketError(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "连接被拒绝",
        SocketError.HostNotFound => "主机名解析失败",
        SocketError.TimedOut => "连接超时",
        SocketError.NetworkUnreachable => "网络不可达",
        _ => e.Message,
    };

    // ─────────────────────────── 协议原语 ───────────────────────────

    private static void WriteVarInt(Stream stream, int value)
    {
        uint v = (uint)value;
        while ((v & ~0x7Fu) != 0)
        {
            stream.WriteByte((byte)((v & 0x7F) | 0x80));
            v >>= 7;
        }
        stream.WriteByte((byte)v);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static int VarIntSize(int value)
    {
        uint v = (uint)value;
        int count = 1;
        while ((v & ~0x7Fu) != 0)
        {
            v >>= 7;
            count++;
        }
        return count;
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        int result = 0;
        var one = new byte[1];
        for (int shift = 0; shift < 35; shift += 7)
        {
            int n = await stream.ReadAsync(one, ct).ConfigureAwait(false);
            if (n <= 0) throw new IOException("读取 varint 时连接提前关闭");
            byte b = one[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new IOException("varint 过长");
    }
}
