using System.Net;
using System.Text;
using System.Text.Json;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core.Sync.Rpu;

/// <summary>一次 HTTP 交互的受限读取上限（清单信封按大额度放行）。</summary>
internal static class RpuLimits
{
    public const int ManifestBytes = 64 * 1024 * 1024;
    public const int JsonBytes = 4 * 1024 * 1024;
}

/// <summary>
/// RPU 在线客户端：会话令牌管理 + 各只读/下单接口 + 重试与错误文案。
/// 接口形状与 <c>qlrpu-client</c> Java 端逐一对齐（sessions/latest/manifest/sources/check/tickets/keys）。
/// </summary>
public sealed class RpuClient : IDisposable
{
    /// <summary>会话令牌在到期前保留的余量（与服务端语义一致的提前换签窗口）。</summary>
    private static readonly TimeSpan SessionSlack = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TicketMargin = TimeSpan.FromSeconds(5);

    private const string ModVersion = "1.0.0";
    private const string MinecraftVersion = "1.20.1";
    /// <summary>RPU 客户端 UA（Java 端硬编码字面量，与 modVersion 1.0.0 无关）。</summary>
    public const string UserAgent = "QingLanRPU/1.0 protocol/1";

    private readonly HttpClient _http;
    private RpuLiveSession? _session;

    public string ApiBase { get; }

    public RpuClient(string apiBase)
    {
        ApiBase = NormalizeApiBase(apiBase);
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
    }

    /// <summary>校验并规范化 apiBase：必须以 /api/v1/client 结尾；http 仅允许本机回环。</summary>
    public static string NormalizeApiBase(string? apiBase)
    {
        var trimmed = (apiBase ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new RpuException("更新服务地址无效，请检查配置。");
        if (uri.Scheme == Uri.UriSchemeHttp
            && !IPAddress.IsLoopback(uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6
                ? IPAddress.Parse(uri.Host) : ResolveLoopbackOrThrow(uri.Host)))
            throw new RpuException("更新服务地址无效：HTTP 仅允许本机调试。");
        if (!trimmed.EndsWith("/api/v1/client", StringComparison.Ordinal))
            throw new RpuException("更新服务地址无效：必须指向 /api/v1/client。");
        return trimmed;
    }

    private static IPAddress ResolveLoopbackOrThrow(string host)
    {
        if (host is "localhost")
            return IPAddress.Loopback;
        throw new RpuException("更新服务地址无效：HTTP 仅允许本机调试。");
    }

    public void Dispose() => _http.Dispose();

    // ────────────────────────────── 对外接口 ──────────────────────────────

    /// <summary>GET /releases/latest：当前发布标识。</summary>
    public async Task<RpuLatest> GetLatestAsync(CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"/releases/latest", RpuLimits.JsonBytes, ct).ConfigureAwait(false);
        using var _ = doc;
        var root = doc.RootElement;
        int version = root.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.Number
            ? pv.GetInt32() : 0;
        if (version != RpuProtocol.Version)
            throw new RpuException("客户端协议版本不兼容，请更新 Mod。");
        var releaseId = root.TryGetProperty("releaseId", out var rid) && rid.ValueKind == JsonValueKind.String
            ? rid.GetString() : null;
        if (!RpuProtocol.IdOk(releaseId))
            throw new RpuException("更新服务响应格式无效。");
        return new RpuLatest(version, releaseId!);
    }

    /// <summary>GET /releases/&lt;id&gt;/manifest：原始信封（原文落地 current.json 时保持字节一致）。</summary>
    public async Task<(RpuEnvelope Envelope, string RawJson)> GetEnvelopeAsync(string releaseId,
        CancellationToken ct = default)
    {
        var raw = await GetRawAsync($"/releases/{releaseId}/manifest", RpuLimits.ManifestBytes, ct).ConfigureAwait(false);
        RpuEnvelope env;
        try
        {
            env = JsonSerializer.Deserialize<RpuEnvelope>(raw) ?? throw new JsonException();
            if (string.IsNullOrEmpty(env.Payload) || string.IsNullOrEmpty(env.Signature))
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new RpuException("更新服务响应格式无效。");
        }
        return (env, raw);
    }

    /// <summary>GET /sources?releaseId=…：可用下载源（按优先级降序）。</summary>
    public async Task<IReadOnlyList<RpuSource>> GetSourcesAsync(string releaseId, CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"/sources?releaseId={Uri.EscapeDataString(releaseId)}",
            RpuLimits.JsonBytes, ct).ConfigureAwait(false);
        using var _ = doc;
        var list = new List<RpuSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in sources.EnumerateArray())
            {
                var id = el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
                var name = el.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() : null;
                bool available = el.TryGetProperty("available", out var avEl) && avEl.ValueKind == JsonValueKind.True;
                var probeUrl = el.TryGetProperty("probeUrl", out var puEl) && puEl.ValueKind == JsonValueKind.String
                    ? puEl.GetString() : null;
                if (!RpuProtocol.IdOk(id) || name is null || name.Length > 160 || seen.Contains(id!))
                    throw new RpuException("更新服务响应格式无效。");
                if (probeUrl is not null && !QlHttp.IsValidHttpUrl(probeUrl))
                    throw new RpuException("更新服务响应格式无效。");
                seen.Add(id!);
                if (available)
                    list.Add(new RpuSource(id!, name, 0, true, probeUrl));
            }
        }
        if (list.Count == 0)
            throw new RpuException("暂无可用下载源，请稍后重试。");
        return list;
    }

    /// <summary>POST /releases/&lt;id&gt;/check：上报本地缓存校验结果，换取仍需下载的 objectId 集合。</summary>
    public async Task<IReadOnlyList<string>> CheckAsync(string releaseId,
        IReadOnlyList<(string ObjectId, bool Valid, string CipherSha256)> entries, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            objects = entries.Select(e => new { objectId = e.ObjectId, valid = e.Valid, cipherSha256 = e.CipherSha256 })
                .ToArray(),
        });
        var doc = await PostJsonAsync($"/releases/{releaseId}/check", body, RpuLimits.JsonBytes, ct).ConfigureAwait(false);
        using var _ = doc;
        if (!doc.RootElement.TryGetProperty("requiredObjectIds", out var req) || req.ValueKind != JsonValueKind.Array)
            throw new RpuException("更新服务响应格式无效。");
        var result = new List<string>();
        foreach (var el in req.EnumerateArray())
        {
            var id = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
            if (!RpuProtocol.IdOk(id))
                throw new RpuException("更新服务响应格式无效。");
            result.Add(id!);
        }
        return result;
    }

    /// <summary>POST /download-tickets：≤128 个 objectId 一批，换取带签名的临时下载 URL。</summary>
    public async Task<IReadOnlyList<RpuTicket>> GetTicketsAsync(string releaseId, string sourceId,
        IReadOnlyList<string> objectIds, CancellationToken ct = default)
    {
        if (objectIds.Count == 0 || objectIds.Count > 128)
            throw new RpuException("内部错误：下载批次大小非法。");
        if (objectIds.Distinct(StringComparer.Ordinal).Count() != objectIds.Count)
            throw new RpuException("内部错误：下载批次含重复对象。");
        var body = JsonSerializer.Serialize(new { releaseId, sourceId, objectIds });
        var doc = await PostJsonAsync("/download-tickets", body, RpuLimits.JsonBytes, ct).ConfigureAwait(false);
        using var _ = doc;
        if (!doc.RootElement.TryGetProperty("tickets", out var tickets) || tickets.ValueKind != JsonValueKind.Array)
            throw new RpuException("更新服务响应格式无效。");
        var result = new List<RpuTicket>();
        foreach (var el in tickets.EnumerateArray())
        {
            var id = el.TryGetProperty("objectId", out var idEl) && idEl.ValueKind == JsonValueKind.String ? idEl.GetString() : null;
            var url = el.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
            var expires = el.TryGetProperty("expiresAt", out var exEl) && exEl.ValueKind == JsonValueKind.String ? exEl.GetString() : null;
            if (!RpuProtocol.IdOk(id) || url is null || !QlHttp.IsValidHttpUrl(url)
                || !DateTimeOffset.TryParse(expires, out var exp) || exp <= DateTimeOffset.UtcNow)
                throw new RpuException("更新服务响应格式无效。");
            result.Add(new RpuTicket(id!, url, expires!));
        }
        if (result.Count != objectIds.Count
            || result.Select(t => t.ObjectId).Distinct(StringComparer.Ordinal).Count() != objectIds.Count
            || !objectIds.All(result.Select(t => t.ObjectId).Contains))
            throw new RpuException("更新服务响应格式无效。");
        return result;
    }

    /// <summary>工单是否仍在有效期（下载前再校验一次，留 5 秒余量）。</summary>
    public static bool TicketUsable(RpuTicket ticket)
        => DateTimeOffset.TryParse(ticket.ExpiresAt, out var exp)
           && exp > DateTimeOffset.UtcNow + TicketMargin;

    /// <summary>POST /releases/&lt;id&gt;/keys：拉取对象解密密钥（32B）。</summary>
    public async Task<IReadOnlyDictionary<string, byte[]>> GetKeysAsync(string releaseId,
        IReadOnlyList<string> objectIds, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { objectIds });
        var doc = await PostJsonAsync($"/releases/{releaseId}/keys", body, RpuLimits.JsonBytes, ct).ConfigureAwait(false);
        using var _ = doc;
        if (!doc.RootElement.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Object)
            throw new RpuException("更新服务响应格式无效。");
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var prop in keys.EnumerateObject())
        {
            if (!RpuProtocol.IdOk(prop.Name) || prop.Value.ValueKind != JsonValueKind.String
                || !objectIds.Contains(prop.Name))
                throw new RpuException("更新服务响应格式无效。");
            byte[] key;
            try { key = Convert.FromBase64String(prop.Value.GetString()!); }
            catch (FormatException) { throw new RpuException("更新服务响应格式无效。"); }
            if (key.Length != 32)
                throw new RpuException("更新服务响应格式无效。");
            result[prop.Name] = key;
        }
        if (result.Count != objectIds.Distinct().Count())
            throw new RpuException("更新服务响应格式无效。");
        return result;
    }

    // ────────────────────────────── 会话 ──────────────────────────────

    private sealed record RpuLiveSession(string Token, DateTimeOffset ExpiresUtc);

    /// <summary>确保有效会话（到期前 30 秒内或 force 时重新签发）。</summary>
    private async Task<RpuLiveSession> EnsureSessionAsync(bool force, CancellationToken ct)
    {
        if (!force && _session is { } s && s.ExpiresUtc > DateTimeOffset.UtcNow + SessionSlack)
            return s;
        var doc = await PostJsonAsync("/sessions", JsonSerializer.Serialize(new
        {
            protocolVersion = RpuProtocol.Version,
            modVersion = ModVersion,
            minecraftVersion = MinecraftVersion,
        }), RpuLimits.JsonBytes, ct, authed: false).ConfigureAwait(false);
        using var _ = doc;
        var root = doc.RootElement;
        int version = root.TryGetProperty("protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.Number
            ? pv.GetInt32() : 0;
        if (version != RpuProtocol.Version)
            throw new RpuException("客户端协议版本不兼容，请更新 Mod。");
        var token = root.TryGetProperty("token", out var tok) && tok.ValueKind == JsonValueKind.String ? tok.GetString() : null;
        var expires = root.TryGetProperty("expiresAt", out var ex) && ex.ValueKind == JsonValueKind.String ? ex.GetString() : null;
        if (string.IsNullOrWhiteSpace(token) || token!.Length > 8192
            || !DateTimeOffset.TryParse(expires, out var exp))
            throw new RpuException("更新服务响应格式无效。");
        _session = new RpuLiveSession(token, exp);
        return _session;
    }

    // ────────────────────────────── HTTP 底座 ──────────────────────────────

    private async Task<JsonDocument> GetJsonAsync(string path, int maxBytes, CancellationToken ct)
    {
        var raw = await GetRawAsync(path, maxBytes, ct).ConfigureAwait(false);
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { throw new RpuException("更新服务响应格式无效。"); }
    }

    private async Task<JsonDocument> PostJsonAsync(string path, string body, int maxBytes, CancellationToken ct,
        bool authed = true)
    {
        // 签发会话的 POST（authed=false）不能再走会话前置，否则 EnsureSession ↔ Exchange 无限互递归。
        var raw = await ExchangeAsync(HttpMethod.Post, path, body, maxBytes, authed, ct,
            ensureSession: authed).ConfigureAwait(false);
        try { return JsonDocument.Parse(raw); }
        catch (JsonException) { throw new RpuException("更新服务响应格式无效。"); }
    }

    private Task<string> GetRawAsync(string path, int maxBytes, CancellationToken ct)
        => ExchangeAsync(HttpMethod.Get, path, null, maxBytes, authed: true, ct);

    /// <summary>带会话与重试策略的单次交换：429/503 退避重试（≤2 次，尊重 Retry-After）；
    /// 401 且已有令牌 → 强制重签会话后重试一次；其余非成功码按致命文案抛出。
    /// ensureSession=false 仅用于签发会话自身的请求（无令牌、且不得再进会话前置）。</summary>
    private async Task<string> ExchangeAsync(HttpMethod method, string path, string? body, int maxBytes,
        bool authed, CancellationToken ct, bool ensureSession = true)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var session = ensureSession && authed
                ? await EnsureSessionAsync(force: false, ct).ConfigureAwait(false)
                : null;
            using var req = new HttpRequestMessage(method, ApiBase + path);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
            req.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoStore = true };
            if (authed)
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer",
                    session!.Token);
            if (body is not null)
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.Unauthorized && authed && attempt == 1)
            {
                await EnsureSessionAsync(force: true, ct).ConfigureAwait(false);
                continue;
            }
            if (resp.StatusCode is ((HttpStatusCode)429) or HttpStatusCode.ServiceUnavailable && attempt <= 2)
            {
                var delay = RetryAfterMillis(resp) is { } ms and > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.FromSeconds(1);
                Globals.Log($"RPU 服务端限流({(int)resp.StatusCode})，{delay.TotalMilliseconds:0}ms 后第 {attempt} 次重试: {path}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
                continue;
            }
            ThrowForFatalStatus(resp, path);
            return await ReadBoundedAsync(resp, maxBytes, ct).ConfigureAwait(false);
        }
    }

    private static long? RetryAfterMillis(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d) return (long)d.TotalMilliseconds;
        if (resp.Headers.RetryAfter?.Date is { } date) return (long)(date - DateTimeOffset.UtcNow).TotalMilliseconds;
        return null;
    }

    private static void ThrowForFatalStatus(HttpResponseMessage resp, string path)
    {
        if ((int)resp.StatusCode >= 200 && (int)resp.StatusCode < 300) return;
        throw (int)resp.StatusCode switch
        {
            401 or 403 => new RpuException("在线授权失败"),
            404 => new RpuException("发布或接口不存在"),
            409 or 426 => new RpuException("客户端协议版本不兼容，请更新 Mod。"),
            >= 500 => new RpuException("更新服务暂时不可用，请稍后重试。"),
            _ => new RpuException($"更新服务异常（HTTP {(int)resp.StatusCode}）：{path}"),
        };
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage resp, int maxBytes, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + n > maxBytes)
                throw new RpuException("服务端响应超过允许大小。");
            buffer.Write(chunk, 0, n);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
