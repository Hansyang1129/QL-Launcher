using System.Net.Http.Headers;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core.Auth;

/// <summary>登录链路里可注入的节点地址（真实默认 = 微软/Xbox/MC 官方端点）。</summary>
public sealed record QlMsEndpoints(
    string DeviceCodeUrl,
    string TokenUrl,
    string XblAuthUrl,
    string XstsAuthUrl,
    string McLoginUrl,
    string McProfileUrl)
{
    public static QlMsEndpoints Real { get; } = new(
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
        "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
        "https://user.auth.xboxlive.com/user/authenticate",
        "https://xsts.auth.xboxlive.com/xsts/authorize",
        "https://api.minecraftservices.com/authentication/login_with_xbox",
        "https://api.minecraftservices.com/minecraft/profile");
}

/// <summary>OAuth/账号链路失败。Code 便于 UI 归类提示。</summary>
public sealed class QlAuthException : Exception
{
    public string Code { get; }

    public QlAuthException(string code, string message) : base(message) => Code = code;
}

/// <summary>
/// 微软登录完账：access/refresh（device code 或 refresh 换发）→ XBL → XSTS →
/// MC login_with_xbox → profile(id/name)。结果供启动参数与账号存储使用。
/// </summary>
public sealed record QlMsAccount(
    string McToken,
    DateTimeOffset McTokenExpiresAt,
    string Uuid,
    string Name,
    string MsRefreshToken);

/// <summary>
/// 微软设备码登录引擎（OAuth device flow，零本地回调服务器）。
/// 步骤镜像 HMCL MicrosoftService：XBL RPS → XSTS RETAIL(rp minecraftservices) →
/// identityToken=XBL3.0 x=&lt;uhs&gt;;&lt;token&gt; → /minecraft/profile。
/// </summary>
public sealed class QlMicrosoftAuth
{
    public const string Scope = "XboxLive.signin offline_access";

    private readonly QlMsEndpoints _ep;
    private readonly int _slowDownPenalty; // slow_down 时加等秒数（测试注入 0）

    public QlMicrosoftAuth(QlMsEndpoints? endpoints = null, int slowDownPenalty = 5)
    {
        _ep = endpoints ?? QlDeployment.AuthEndpoints;
        _slowDownPenalty = slowDownPenalty;
    }

    // ───────────────────── 设备码入口 ─────────────────────

    /// <summary>申请设备码。返回 (用户码, 引导网址, device_code, 首次轮询间隔, 截止时刻)。</summary>
    public async Task<(string UserCode, string VerifyUri, string DeviceCode, int Interval, DateTimeOffset Deadline)>
        BeginDeviceSignInAsync(string clientId, CancellationToken ct = default)
    {
        var text = await PostFormAsync(_ep.DeviceCodeUrl, ct,
            ("client_id", clientId), ("scope", Scope)).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var err = Str(root, "error");
        if (err is not null)
            throw new QlAuthException(err, "设备码申请失败: " + err);
        var interval = root.TryGetProperty("interval", out var iv) ? Math.Max(iv.GetInt32(), 0) : 5;
        var expiresIn = root.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 900;
        return (Str(root, "user_code") ?? "",
                Str(root, "verification_uri") ?? "",
                Str(root, "device_code") ?? "",
                interval,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn, 1)));
    }

    /// <summary>
    /// 轮询设备码直至授权完成并跑完 XBL→XSTS→MC→profile 链。
    /// onPrompt 首次回调 <c>(user_code, verify_uri)</c>；onPoll 回调当前尝试编号。
    /// </summary>
    public async Task<QlMsAccount> DeviceSignInAsync(string clientId, string deviceCode,
        int intervalSeconds, DateTimeOffset deadline,
        Action<string, string>? onPrompt = null, Action<int>? onPoll = null,
        CancellationToken ct = default)
    {
        for (int attempt = 1; ; attempt++)
        {
            if (DateTimeOffset.UtcNow >= deadline)
                throw new QlAuthException("expired_token", "设备码已过期，请重新登录");

            var text = await PostFormAsync(_ep.TokenUrl, ct,
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
                ("device_code", deviceCode),
                ("client_id", clientId)).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var access = Str(root, "access_token");

            if (access is null)
            {
                var err = Str(root, "error") ?? "unknown";
                switch (err)
                {
                    case "authorization_pending":
                        onPoll?.Invoke(attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(intervalSeconds, 0)), ct)
                            .ConfigureAwait(false);
                        continue;
                    case "slow_down":
                        intervalSeconds += _slowDownPenalty;
                        onPoll?.Invoke(attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(intervalSeconds, 0)), ct)
                            .ConfigureAwait(false);
                        continue;
                    case "expired_token":
                        throw new QlAuthException(err, "设备码已过期，请重新登录");
                    case "authorization_declined":
                        throw new QlAuthException(err, "用户在微软页面拒绝了授权");
                    default:
                        throw new QlAuthException(err, "登录等待失败: " + err);
                }
            }

            var refresh = Str(root, "refresh_token")
                ?? throw new QlAuthException("no_refresh", "微软登录缺少 refresh_token（scope 需 offline_access）");
            return await CompleteAsync(access, refresh, ct).ConfigureAwait(false);
        }
    }

    /// <summary>一次走完：申请设备码 + 轮询 + 全链换发。onPrompt 在拿到用户码时回调。</summary>
    public async Task<QlMsAccount> SignInAsync(string clientId,
        Action<string, string>? onPrompt = null, CancellationToken ct = default)
    {
        var begin = await BeginDeviceSignInAsync(clientId, ct).ConfigureAwait(false);
        onPrompt?.Invoke(begin.UserCode, begin.VerifyUri);
        return await DeviceSignInAsync(clientId, begin.DeviceCode, begin.Interval, begin.Deadline,
            onPrompt: null, ct: ct).ConfigureAwait(false);
    }

    /// <summary>静默续期：refresh_token 换新会话。invalid_grant 让 UI 引导重新登录。</summary>
    public async Task<QlMsAccount> RefreshAsync(string clientId, string refreshToken,
        CancellationToken ct = default)
    {
        var text = await PostFormAsync(_ep.TokenUrl, ct,
            ("grant_type", "refresh_token"),
            ("client_id", clientId),
            ("refresh_token", refreshToken),
            ("scope", Scope)).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var access = Str(root, "access_token")
            ?? throw new QlAuthException(Str(root, "error") ?? "invalid_grant", "登录已过期，请重新登录");
        var fresh = Str(root, "refresh_token") ?? refreshToken; // 未发新就沿用
        return await CompleteAsync(access, fresh, ct).ConfigureAwait(false);
    }

    // ───────────────────── XBL → XSTS → MC → profile ─────────────────────

    private async Task<QlMsAccount> CompleteAsync(string msToken, string refresh, CancellationToken ct)
    {
        // 1) XBL：用户令牌 + 用户哈希 uhs
        var xblJson = await PostJsonAsync(_ep.XblAuthUrl,
            """
            {"Properties":{"AuthMethod":"RPS","SiteName":"user.auth.xboxlive.com","RpsTicket":"d=$MS$"},
             "RelyingParty":"http://auth.xboxlive.com","TokenType":"JWT"}
            """.Replace("$MS$", msToken), ct).ConfigureAwait(false);
        using (var doc = JsonDocument.Parse(xblJson))
        {
            var root = doc.RootElement;
            var xblToken = Str(root, "Token") ?? throw new QlAuthException("xbl", "XBL 认证失败");
            var uhs = root.TryGetProperty("DisplayClaims", out var dc) &&
                      dc.TryGetProperty("xui", out var xui) &&
                      xui.ValueKind == JsonValueKind.Array &&
                      xui.GetArrayLength() > 0 &&
                      xui[0].TryGetProperty("uhs", out var u)
                ? u.GetString() ?? ""
                : throw new QlAuthException("xbl_uhs", "XBL 缺少用户哈希");

            // 2) XSTS：RETAIL + minecraftservices 依赖方
            var xstsJson = await PostJsonAsync(_ep.XstsAuthUrl,
                """
                {"Properties":{"SandboxId":"RETAIL","UserTokens":["$T$"]},
                 "RelyingParty":"rp://api.minecraftservices.com/","TokenType":"JWT"}
                """.Replace("$T$", xblToken), ct,
                onFail: body => DescribeXErr(body)).ConfigureAwait(false);
            using var doc2 = JsonDocument.Parse(xstsJson);
            var xstsToken = Str(doc2.RootElement, "Token") ?? throw new QlAuthException("xsts", "XSTS 授权失败");

            // 3) MC 令牌
            var mcJson = await PostJsonAsync(_ep.McLoginUrl,
                """
                {"identityToken":"XBL3.0 x=$UHS$;$T$"}
                """.Replace("$UHS$", uhs).Replace("$T$", xstsToken), ct).ConfigureAwait(false);
            using var doc3 = JsonDocument.Parse(mcJson);
            var mcToken = Str(doc3.RootElement, "access_token")
                ?? throw new QlAuthException("mc_login", "Minecraft 令牌换发失败");
            var expiresIn = doc3.RootElement.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 86400;

            // 4) Java 版档案
            using var req = new HttpRequestMessage(HttpMethod.Get, _ep.McProfileUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
            using var resp = await QlHttp.Client.SendAsync(req, ct).ConfigureAwait(false);
            var profileText = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            QlAuthException Fail(string code, string msg) => new(code, msg);
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw Fail("no_profile", "该账号没有 Minecraft Java 版档案");
            resp.EnsureSuccessStatusCode();
            using var pd = JsonDocument.Parse(profileText);
            var name = Str(pd.RootElement, "name")
                ?? throw Fail("no_profile", "该账号没有 Minecraft Java 版档案");
            var id = Str(pd.RootElement, "id")
                ?? throw Fail("no_profile", "档案缺少 id");
            var uuid = FormatUuid(id);
            return new QlMsAccount(mcToken,
                DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresIn, 60)),
                uuid, name, refresh);
        }
    }

    /// <summary>XSTS 失败体 → 人类可读提示（XErr 语义表照 HMCL）。</summary>
    public static string DescribeXErr(string body)
    {
        long code = 0;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var e))
                code = e.ValueKind == JsonValueKind.Number ? e.GetInt64() : 0;
        }
        catch (JsonException) { /* 读不出就按未知处理 */ }
        var zh = code switch
        {
            2148916233 => "该微软账号还没有 Xbox 账号",
            2148916235 => "所在区域不可用或受 Xbox 成人管控",
            2148916236 => "未成年 Xbox 账号：需要家长把该账号加为好友",
            2148916237 => "未成年 Xbox 账号：需要家长先加为好友",
            2148916238 => "未成年 Xbox 账号，需先解除家长管控",
            _ => "XSTS 授权失败",
        };
        return zh + " (XErr=" + code + ")";
    }

    /// <summary>MC 档案 id（无连字符 hex）→ Java UUID 记法 8-4-4-4-12。</summary>
    public static string FormatUuid(string hex)
    {
        var s = hex.Replace("-", "").ToLowerInvariant();
        if (s.Length != 32) return hex;
        return s[..8] + "-" + s.Substring(8, 4) + "-" + s.Substring(12, 4) + "-"
               + s.Substring(16, 4) + "-" + s[20..];
    }

    // ───────────────────── HTTP 小助手 ─────────────────────

    private static async Task<string> PostFormAsync(string url,
        CancellationToken ct, params (string K, string V)[] fields)
    {
        var body = string.Join("&", fields.Select(f =>
            Uri.EscapeDataString(f.K) + "=" + Uri.EscapeDataString(f.V)));
        using var resp = await QlHttp.Client.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"), ct)
            .ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        return text;
    }

    private static async Task<string> PostJsonAsync(string url, string json, CancellationToken ct,
        Func<string, string>? onFail = null)
    {
        using var content = new StringContent(json, new MediaTypeHeaderValue("application/json"));
        using var resp = await QlHttp.Client.PostAsync(url, content, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new QlAuthException("xsts", onFail?.Invoke(text) ?? "请求失败 (" + (int)resp.StatusCode + ")");
        return text;
    }

    private static string? Str(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
