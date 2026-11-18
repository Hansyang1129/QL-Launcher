using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Qinglan.Launcher.Core.Status;

namespace Qinglan.Tests;

/// <summary>M3 服务器状态：SLP 握手/分帧/解析、HTTP 状态语义、超时与批量隔离、MOTD 摊平。</summary>
internal static class StatusSuite
{
    public static void Run()
    {
        T.Group("M3 SLP 协议与解析");
        SlpProtocol().GetAwaiter().GetResult();
        T.Group("M3 SLP 异常与超时");
        SlpFailures().GetAwaiter().GetResult();
        T.Group("M3 HTTP 探测");
        HttpProbes().GetAwaiter().GetResult();
        T.Group("M3 批量探测与默认表");
        BatchAndDefaults().GetAwaiter().GetResult();
        T.Group("M3 MOTD 摊平");
        MotdFlatten();
    }

    private const string StatusJson = """
        {"version":{"name":"1.20.1","protocol":763},"players":{"max":100,"online":7,"sample":[{"name":"Alice"},{"name":"Bob"}]},"description":{"text":"轻蓝 ","extra":[{"text":"MTR","extra":[{"text":"\n第二行"}]}]}}
        """;

    // ─────────────────────────── SLP 正常路径 ───────────────────────────

    private static async Task SlpProtocol()
    {
        using var fake = new FakeSlpServer(s => s.WriteAsync(StatusFrame(StatusJson)).AsTask());
        var result = await QlServerStatus.PingSlpAsync("127.0.0.1", fake.Port, 3000);

        T.CheckEq("在线人数", 7, result.PlayersOnline);
        T.CheckEq("槽位上限", 100, result.PlayersMax);
        T.CheckEq("MOTD 递归摊平", "轻蓝 MTR\n第二行", result.Motd);
        T.CheckEq("玩家样本", "Alice,Bob", string.Join(",", result.Sample));
        T.Check("延迟非负", result.LatencyMs >= 0);

        await fake.HandshakeSeen.WaitAsync(TimeSpan.FromSeconds(5));
        var hs = fake.Handshake!;
        int idx = 0;
        T.CheckEq("握手包 id 0", 0, ReadVarInt(hs, ref idx));
        T.CheckEq("协议号 47", 47, ReadVarInt(hs, ref idx));
        T.CheckEq("握手主机", "127.0.0.1", ReadString(hs, ref idx));
        int port = (hs[idx] << 8) | hs[idx + 1];
        idx += 2;
        T.CheckEq("握手端口", fake.Port, port);
        T.CheckEq("下一状态 1", 1, ReadVarInt(hs, ref idx));
        T.CheckEq("状态请求载荷", "00", Convert.ToHexString(fake.StatusRequest!));
        T.CheckEq("状态请求包长", 1, fake.StatusLength);

        var probe = result.ToProbeResult();
        T.Check("SLP 结果转 UI 形状", probe.Online && probe.Players == 7 &&
            probe.PlayersMax == 100 && probe.Detail == result.Motd);
    }

    // ─────────────────────────── SLP 异常路径 ───────────────────────────

    private static async Task SlpFailures()
    {
        // stringLength(100) 大于 packetLength 能容纳的正文 → 必须拒绝而不是越界分配。
        using var malformed = new FakeSlpServer(s =>
        {
            using var payload = new MemoryStream();
            WriteVarInt(payload, 0);
            WriteVarInt(payload, 100);
            payload.Write("{}"u8);
            return s.WriteAsync(Framed(payload.ToArray())).AsTask();
        });
        var bad = await ProbeAllAsync(QlProbe.Slp("bad", "127.0.0.1", malformed.Port), 2000);
        T.Check("畸形分帧 → 离线", !bad.Online);
        T.Check("畸形分帧给出分帧错误", bad.Detail?.Contains("分帧", StringComparison.Ordinal) == true);

        // 六个续位字节的 varint 属于非法编码。
        using var garbage = new FakeSlpServer(s =>
            s.WriteAsync(new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x80 }).AsTask());
        var over = await ProbeAllAsync(QlProbe.Slp("varint", "127.0.0.1", garbage.Port), 2000);
        T.Check("超长 varint → 离线", !over.Online);
        T.Check("超长 varint 给出原因", over.Detail?.Contains("varint", StringComparison.Ordinal) == true);

        // 接受连接但永不响应：超时收敛为离线，错误文案带超时。
        using var silent = new FakeSlpServer(_ => Task.Delay(3000));
        var timeout = await ProbeAllAsync(QlProbe.Slp("slow", "127.0.0.1", silent.Port), 300);
        T.Check("读取超时 → 离线", !timeout.Online);
        T.Check("超时给出超时原因", timeout.Detail?.Contains("超时", StringComparison.Ordinal) == true);

        // 未监听的端口：连接被拒绝。
        int closedPort = ReserveClosedPort();
        var refused = await ProbeAllAsync(QlProbe.Slp("closed", "127.0.0.1", closedPort), 1000);
        T.Check("连接拒绝 → 离线", !refused.Online);
        T.Check("连接拒绝给出原因", !string.IsNullOrWhiteSpace(refused.Detail));
    }

    // ─────────────────────────── HTTP ───────────────────────────

    private static async Task HttpProbes()
    {
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        srv.AddFile("/up", Encoding.UTF8.GetBytes("ok"));
        srv.FailPath("/down", 500);
        srv.FailPath("/missing", 404);
        srv.DelayPath("/slow");
        srv.AddFile("/slow", Encoding.UTF8.GetBytes("late"));

        var up = await QlServerStatus.ProbeHttpAsync(srv.BaseUrl + "up", 3000);
        T.Check("HTTP 200 在线", up.IsUp);
        T.CheckEq("状态码透传", 200, up.StatusCode);
        T.Check("延迟非负", up.LatencyMs >= 0);
        T.CheckEq("UI detail 为 HTTP 码", "HTTP 200", up.ToProbeResult().Detail);

        var down = await QlServerStatus.ProbeHttpAsync(srv.BaseUrl + "down", 3000);
        T.Check("HTTP 500 不算在线", !down.IsUp);
        T.CheckEq("错误状态码保留", 500, down.StatusCode);

        var missing = await QlServerStatus.ProbeHttpAsync(srv.BaseUrl + "missing", 3000);
        T.Check("HTTP 404 不算在线", !missing.IsUp && missing.StatusCode == 404);

        var slow = await ProbeAllAsync(QlProbe.Http("slow", "127.0.0.1", 0, srv.BaseUrl + "slow"), 300);
        T.Check("HTTP 超时 → 离线", !slow.Online);
        T.Check("HTTP 超时给出原因", slow.Detail?.Contains("超时", StringComparison.Ordinal) == true);

        var invalid = await ProbeAllAsync(QlProbe.Http("bad-url", "h", 0, "not a url"), 1000);
        T.Check("非法 URL → 离线", !invalid.Online);
        T.Check("非法 URL 给出原因", invalid.Detail?.Contains("URL", StringComparison.Ordinal) == true);

        var ftp = await ProbeAllAsync(QlProbe.Http("ftp", "h", 0, "ftp://example.com/x"), 1000);
        T.Check("非 HTTP(S) 协议 → 离线", !ftp.Online);
    }

    // ─────────────────────────── 批量与默认表 ───────────────────────────

    private static async Task BatchAndDefaults()
    {
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        srv.AddFile("/up", Encoding.UTF8.GetBytes("ok"));
        srv.FailPath("/down", 503);
        int closedPort = ReserveClosedPort();

        var probes = new[]
        {
            QlProbe.Http("up", "127.0.0.1", 0, srv.BaseUrl + "up"),
            QlProbe.Http("down", "127.0.0.1", 0, srv.BaseUrl + "down"),
            QlProbe.Slp("closed", "127.0.0.1", closedPort),
        };
        var results = await QlServerStatus.ProbeAllAsync(probes, 1000);
        T.CheckEq("批量结果与探测表等长", 3, results.Count);
        T.Check("首点在线", results[0].Online);
        T.Check("次点离线且保留 HTTP 码", !results[1].Online && results[1].Detail == "HTTP 503");
        T.Check("单点异常不拖垮整表", !results[2].Online && results[2].Detail?.Length > 0);
        T.Check("真实在线数统计正确", results.Count(r => r.Online) == 1);

        var defs = QlServerStatus.DefaultProbes;
        T.CheckEq("默认端点 3 个", 3, defs.Count);
        T.Check("轻蓝端点 = SLP mc.example.com:32650",
            defs[0].Kind == QlProbeKind.Slp && defs[0].Host == "mc.example.com" && defs[0].Port == 32650);
        T.CheckEq("MTR Web 是 HTTP", QlProbeKind.Http, defs[1].Kind);
        T.Check("Reporter 是 HTTP", defs[2].Kind == QlProbeKind.Http && defs[2].Port == 19451);
        T.CheckEq("空白标签回落 host:port", "mc.example.com:32650",
            new QlProbe(" ", QlProbeKind.Slp, "mc.example.com", 32650, null).DisplayLabel);
        T.Check("MTR 探真实地图 API（根路径 404）", defs[1].Address.Contains(
            "/mtr/api/map/stations-and-routes", StringComparison.Ordinal));
        T.CheckEq("Reporter 探登录页（根路径 401）", "http://mc.example.com:19451/login",
            defs[2].Address);
    }

    // ─────────────────────────── MOTD ───────────────────────────

    private static void MotdFlatten()
    {
        using var plain = JsonDocument.Parse("\"纯文本\"");
        T.CheckEq("字符串 MOTD", "纯文本", QlServerStatus.FlattenText(plain.RootElement));

        using var nested = JsonDocument.Parse(
            """{"text":"A","extra":[{"text":"B","extra":[{"text":"C"}]},{"text":"D"}]}""");
        T.CheckEq("嵌套 extra 全量拼接", "ABCD", QlServerStatus.FlattenText(nested.RootElement));

        using var noText = JsonDocument.Parse("""{"extra":[{"text":"只有附加"}]}""");
        T.CheckEq("缺 text 仍读 extra", "只有附加", QlServerStatus.FlattenText(noText.RootElement));

        using var number = JsonDocument.Parse("42");
        T.CheckEq("数字原语按文本处理", "42", QlServerStatus.FlattenText(number.RootElement));

        using var array = JsonDocument.Parse("""["不","支持"]""");
        T.CheckEq("数组形态给空串（与 QL-HMCL 同口径）", "", QlServerStatus.FlattenText(array.RootElement));
    }

    // ─────────────────────────── 工具 ───────────────────────────

    private static async Task<QlProbeResult> ProbeAllAsync(QlProbe probe, int timeoutMs)
    {
        var list = await QlServerStatus.ProbeAllAsync(new[] { probe }, timeoutMs);
        return list[0];
    }

    private static int ReserveClosedPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static byte[] StatusFrame(string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        using var payload = new MemoryStream();
        WriteVarInt(payload, 0);
        WriteVarInt(payload, body.Length);
        payload.Write(body);
        return Framed(payload.ToArray());
    }

    private static byte[] Framed(byte[] packet)
    {
        using var frame = new MemoryStream();
        WriteVarInt(frame, packet.Length);
        frame.Write(packet);
        return frame.ToArray();
    }

    private static int ReadVarInt(byte[] data, ref int offset)
    {
        int result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = data[offset++];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("varint 过长");
    }

    private static string ReadString(byte[] data, ref int offset)
    {
        int length = ReadVarInt(data, ref offset);
        var value = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return value;
    }

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

    private static async Task<int> ReadVarIntAsync(NetworkStream stream)
    {
        int result = 0;
        var one = new byte[1];
        for (int shift = 0; shift < 35; shift += 7)
        {
            int n = await stream.ReadAsync(one);
            if (n <= 0) throw new IOException("连接提前关闭");
            byte b = one[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new IOException("varint 过长");
    }

    /// <summary>小型假 SLP 服务端：读一帧握手 + 一帧状态请求，记录原始字节后交给脚本应答。</summary>
    private sealed class FakeSlpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _serve;
        private readonly Func<NetworkStream, Task> _afterStatus;
        private readonly TaskCompletionSource<bool> _handshakeSeen =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeSlpServer(Func<NetworkStream, Task> afterStatus)
        {
            _afterStatus = afterStatus;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _serve = Task.Run(ServeAsync);
        }

        public int Port { get; }
        public byte[]? Handshake { get; private set; }
        public byte[]? StatusRequest { get; private set; }
        public int StatusLength { get; private set; }
        public Task HandshakeSeen => _handshakeSeen.Task;

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                var stream = client.GetStream();

                int length = await ReadVarIntAsync(stream);
                Handshake = new byte[length];
                await stream.ReadExactlyAsync(Handshake, _cts.Token);

                int statusLength = await ReadVarIntAsync(stream);
                StatusRequest = new byte[statusLength];
                await stream.ReadExactlyAsync(StatusRequest, _cts.Token);
                StatusLength = statusLength;
                _handshakeSeen.TrySetResult(true);

                await _afterStatus(stream);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _serve.Wait(1500); } catch { /* 停机不外漏 */ }
            _cts.Dispose();
        }
    }
}
