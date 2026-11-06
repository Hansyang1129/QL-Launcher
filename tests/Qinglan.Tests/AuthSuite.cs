using System.Text.Json;
using Qinglan.Launcher.Core.Auth;

namespace Qinglan.Tests;

/// <summary>微软登录引擎：设备码轮询（pending/slow_down/成功）、XBL→XSTS→MC→profile 请求形状、静默续期。</summary>
internal static class AuthSuite
{
    public static void Run()
    {
        T.Group("微软登录 · 设备码全景");
        FullFlow().GetAwaiter().GetResult();
        T.Group("微软登录 · 单元细节");
        UnitRules();
        T.Group("微软登录 · 静默续期");
        RefreshFlow().GetAwaiter().GetResult();
    }

    private static async Task FullFlow()
    {
        var server = await MiniHttpServer.StartAsync(@"%TEMP%\ql-tmp\auth-fake")
            .ConfigureAwait(false);
        try
        {
            server.AddJsonSequence("/devicecode",
                """{"device_code":"DX","user_code":"QLBY 7CMD","verification_uri":"https://login.example/link","expires_in":900,"interval":0}""");
            server.AddJsonSequence("/token",
                """{"error":"authorization_pending"}""",
                """{"error":"slow_down"}""",
                """{"error":"authorization_pending"}""",
                """{"access_token":"MSAT","refresh_token":"MSRT","expires_in":86400}""");
            server.AddJsonSequence("/xbl",
                """{"Token":"XBLTOKEN","DisplayClaims":{"xui":[{"uhs":"UHS123"}]}}""");
            server.AddJsonSequence("/xsts",
                """{"Token":"XSTSTOKEN","DisplayClaims":{"xui":[{"uhs":"UHS123"}]}}""");
            server.AddJsonSequence("/mclogin",
                """{"access_token":"MCAT","expires_in":108000}""");
            server.AddJsonSequence("/profile",
                """{"id":"b00fce5f1d2a49d0b8b7c5a1f3e2d4c6","name":"QinglanHero"}""");

            var auth = new QlMicrosoftAuth(
                new QlMsEndpoints(server.BaseUrl + "devicecode", server.BaseUrl + "token",
                    server.BaseUrl + "xbl", server.BaseUrl + "xsts",
                    server.BaseUrl + "mclogin", server.BaseUrl + "profile"),
                slowDownPenalty: 0);

            string? shownCode = null, shownUri = null;
            var account = await auth.SignInAsync("TEST-CLIENT", (code, uri) =>
            {
                shownCode = code;
                shownUri = uri;
            }).ConfigureAwait(false);

            T.Check("设备码提示给了用户码", shownCode == "QLBY 7CMD");
            T.Check("设备码提示给了引导网址", shownUri == "https://login.example/link");
            T.CheckEq("终端账号名", "QinglanHero", account.Name);
            T.CheckEq("终端 UUID 连字符化", "b00fce5f-1d2a-49d0-b8b7-c5a1f3e2d4c6", account.Uuid);
            T.CheckEq("MC 令牌贯通", "MCAT", account.McToken);
            T.CheckEq("refresh_token 保管", "MSRT", account.MsRefreshToken);
            T.Check("MC 令牌过期时刻在未来", account.McTokenExpiresAt > DateTimeOffset.UtcNow);

            T.CheckEq("token 轮询 4 次（pending/slow/pending/成功）", 4, server.Hits("/token"));
            T.Check("devicecode 请求带 client_id 与 scope",
                server.Bodies("/devicecode")[0].Contains("client_id=TEST-CLIENT") &&
                server.Bodies("/devicecode")[0].Contains("scope=XboxLive.signin%20offline_access"));
            T.Check("轮询 grant_type 是 device_code 且带 device_code",
                server.Bodies("/token")[0].Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code") &&
                server.Bodies("/token")[0].Contains("device_code=DX"));
            T.Check("XBL 请求是 RPS + d=<ms> 工单",
                server.Bodies("/xbl")[0].Contains("\"AuthMethod\":\"RPS\"") &&
                server.Bodies("/xbl")[0].Contains("\"RpsTicket\":\"d=MSAT\"") &&
                server.Bodies("/xbl")[0].Contains("\"SiteName\":\"user.auth.xboxlive.com\""));
            T.Check("XISTS 请求 RETAIL + XBL 令牌 + minecraftservices 依赖方",
                BodiesOf(server, "/xsts")[0].Contains("\"SandboxId\":\"RETAIL\"") &&
                server.Bodies("/xsts")[0].Contains("\"UserTokens\":[\"XBLTOKEN\"]") &&
                server.Bodies("/xsts")[0].Contains("rp://api.minecraftservices.com/"));
            T.Check("MC 换发带 XBL3.0 x=<uhs>;<xsts>",
                server.Bodies("/mclogin")[0].Contains("\"identityToken\":\"XBL3.0 x=UHS123;XSTSTOKEN\""));
            T.Check("profile 用 Bearer MCAT",
                server.Headers("/profile")[0].Contains("Authorization: Bearer MCAT"));
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string[] BodiesOf(MiniHttpServer server, string path)
        => server.Bodies(path).ToArray();

    private static void UnitRules()
    {
        T.Check("XErr 2148916238 → 未成年管控提示",
            QlMicrosoftAuth.DescribeXErr("""{"XErr":2148916238}""").Contains("家长管控")
            && QlMicrosoftAuth.DescribeXErr("""{"XErr":2148916238}""").Contains("XErr=2148916238"));
        T.Check("XErr 2148916233 → 没有 Xbox 账号提示",
            QlMicrosoftAuth.DescribeXErr("""{"XErr":2148916233}""").Contains("Xbox 账号"));
        T.Check("XErr 未知 → 通用提示",
            QlMicrosoftAuth.DescribeXErr("""{"XErr":999}""").Contains("XSTS 授权失败"));
        T.CheckEq("档案 id 32hex → 连字符 UUID",
            "b00fce5f-1d2a-49d0-b8b7-c5a1f3e2d4c6",
            QlMicrosoftAuth.FormatUuid("b00fce5f1d2a49d0b8b7c5a1f3e2d4c6"));
        T.Check("档案 id 不合法原样返回",
            QlMicrosoftAuth.FormatUuid("notahex") == "notahex");
    }

    private static async Task RefreshFlow()
    {
        var server = await MiniHttpServer.StartAsync(@"%TEMP%\ql-tmp\auth-fake2")
            .ConfigureAwait(false);
        try
        {
            server.AddJsonSequence("/token",
                """{"access_token":"MSAT2","refresh_token":"MSRT2","expires_in":36000}""");
            server.AddJsonSequence("/xbl", """{"Token":"T2","DisplayClaims":{"xui":[{"uhs":"U2"}]}}""");
            server.AddJsonSequence("/xsts", """{"Token":"S2","DisplayClaims":{"xui":[{"uhs":"U2"}]}}""");
            server.AddJsonSequence("/mclogin", """{"access_token":"MCAT2","expires_in":36000}""");
            server.AddJsonSequence("/profile", """{"id":"fedcba0987654321fedcba0987654321","name":"Qhero2"}""");

            var auth = new QlMicrosoftAuth(
                new QlMsEndpoints(server.BaseUrl + "devicecode", server.BaseUrl + "token",
                    server.BaseUrl + "xbl", server.BaseUrl + "xsts",
                    server.BaseUrl + "mclogin", server.BaseUrl + "profile"),
                slowDownPenalty: 0);

            var account = await auth.RefreshAsync("TEST-CLIENT", "MSRT").ConfigureAwait(false);

            T.CheckEq("续期后 MC 令牌", "MCAT2", account.McToken);
            T.Check("续期回发新 refresh_token 则更新", account.MsRefreshToken == "MSRT2");
            T.Check("续期请求 grant_type=refresh_token 且带旧令牌",
                server.Bodies("/token")[0].Contains("grant_type=refresh_token") &&
                server.Bodies("/token")[0].Contains("refresh_token=MSRT"));
            T.CheckEq("续期后档案串通", "Qhero2", account.Name);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
