using System.Text.Json;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Status;

namespace Qinglan.Tests;

/// <summary>部署配置层：空壳默认、地址归一化、模块判定、探测表由配置生成、登录端点回落。</summary>
internal static class DeploymentSuite
{
    public static void Run()
    {
        T.Group("部署配置：空壳");
        EmptyShell();
        T.Group("部署配置：归一化与判定");
        NormalizeAndGate();
        T.Group("部署配置：探测表");
        ProbesFromConfig();
        T.Group("部署配置：登录端点");
        AuthFallback();
        T.Group("部署配置：JSON 装载");
        ParseConfig();
        T.Group("部署配置：内嵌兜底");
        EmbeddedFallback();
        QlDeployment.Reset();
    }

    private static void EmptyShell()
    {
        QlDeployment.Reset();
        T.Check("空配置无 SLP", !QlDeployment.HasServer);
        T.Check("空配置无 MTR", !QlDeployment.HasMtr);
        T.Check("空配置无论坛", !QlDeployment.HasForum);
        T.Check("空配置无 Reporter", !QlDeployment.HasReporter);
        T.Check("空配置无发布器", !QlDeployment.HasPublisher);
        T.CheckEq("空配置 MTR 根地址为空串", "", QlDeployment.MtrBaseUrl);
        T.CheckEq("空配置论坛根地址为空串", "", QlDeployment.ForumBaseUrl);
        T.Check("空配置探测表为空", QlServerStatus.BuildDefaultProbes().Count == 0);
        T.Check("缺配置提示带字段路径",
            QlDeployment.MissingHint("MTR", "deployment.mtr.baseUrl").Contains("deployment.mtr.baseUrl"));
        T.Check("论坛未配置时帖子保留相对地址",
            QlDeployment.ForumUrl("/archives/a") == "/archives/a");
        T.Check("空配置服务器名兜底",
            QlDeployment.ServerName == "Minecraft");
    }

    private static void NormalizeAndGate()
    {
        QlDeployment.Set(new QlDeploymentConfig
        {
            Server = new QlServerDeployment { Name = " 测试服 ", SlpHost = " 127.0.0.1 ", SlpPort = 25565 },
            Mtr = new QlEndpointDeployment { BaseUrl = " http://127.0.0.1:8080/// " },
            Reporter = new QlEndpointDeployment { BaseUrl = " http://127.0.0.1:8081/ " },
            Forum = new QlEndpointDeployment { BaseUrl = " https://forum.example " },
            Sync = new QlSyncDeployment { PublisherBaseUrl = " https://files.example/base/ " },
        });

        T.CheckEq("服务器名去空白", "测试服", QlDeployment.ServerName);
        T.Check("SLP 就绪", QlDeployment.HasServer);
        T.CheckEq("MTR 地址去尾部斜杠", "http://127.0.0.1:8080", QlDeployment.MtrBaseUrl);
        T.CheckEq("MTR 网页入口补斜杠", "http://127.0.0.1:8080/", QlDeployment.MtrWebUrl);
        T.CheckEq("Reporter 地址去尾部斜杠", "http://127.0.0.1:8081", QlDeployment.ReporterBaseUrl);
        T.CheckEq("论坛地址去尾部斜杠", "https://forum.example", QlDeployment.ForumBaseUrl);
        T.CheckEq("发布器地址去尾部斜杠", "https://files.example/base", QlDeployment.PublisherBaseUrl);
        T.CheckEq("论坛帖子拼绝对地址", "https://forum.example/archives/a",
            QlDeployment.ForumUrl("/archives/a"));

        QlDeployment.Set(new QlDeploymentConfig
        {
            Mtr = new QlEndpointDeployment { BaseUrl = "ftp://not-http" },
            Server = new QlServerDeployment { SlpHost = "host", SlpPort = 70000 },
            Reporter = new QlEndpointDeployment { BaseUrl = "not a url" },
        });
        T.Check("非 http(s) 不算就绪", !QlDeployment.HasMtr);
        T.Check("越界端口不算就绪", !QlDeployment.HasServer);
        T.Check("非绝对地址不算就绪", !QlDeployment.HasReporter);
        T.CheckEq("未就绪时不抛异常，返回空串", "", QlDeployment.MtrBaseUrl);

        bool threw = false;
        try
        {
            QlDeployment.RequireMtrBaseUrl();
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }
        T.Check("Require 未配置时给出可读异常", threw);

        QlDeployment.Set(new QlDeploymentConfig
        {
            Mtr = new QlEndpointDeployment { BaseUrl = "https://mtr.example" },
            Reporter = new QlEndpointDeployment { BaseUrl = "https://mtr.example" },
            Server = new QlServerDeployment { SlpHost = "mc.example", SlpPort = 25566 },
        });
        var probes = QlServerStatus.BuildDefaultProbes();
        T.CheckEq("配置三项生成三个探测", 3, probes.Count);
        T.Check("首项 SLP 来自配置",
            probes[0].Kind == QlProbeKind.Slp && probes[0].Host == "mc.example" && probes[0].Port == 25566);
        T.Check("未填名字时标签回落 host:port", probes[0].DisplayLabel == "mc.example:25566");
        T.Check("MTR 探地图 API", probes[1].Kind == QlProbeKind.Http
            && probes[1].Address == "https://mtr.example/mtr/api/map/stations-and-routes?dimension=minecraft:overworld");
        T.Check("Reporter 探登录页", probes[2].Kind == QlProbeKind.Http
            && probes[2].Address == "https://mtr.example/login" && probes[2].Port == 443);
    }

    private static void ProbesFromConfig()
    {
        QlDeployment.Reset();
        T.Check("空配置不生成探测项", QlServerStatus.BuildDefaultProbes().Count == 0);

        QlDeployment.Set(new QlDeploymentConfig
        {
            Server = new QlServerDeployment { SlpHost = "mc.example", SlpPort = 25565 },
        });
        var onlySlp = QlServerStatus.BuildDefaultProbes();
        T.CheckEq("只配服务器时一个探测", 1, onlySlp.Count);
        T.CheckEq("未填名字时 SLP 标签回落 host:port", "mc.example:25565", onlySlp[0].DisplayLabel);

        QlDeployment.Set(new QlDeploymentConfig
        {
            Server = new QlServerDeployment { Name = "轻蓝", SlpHost = "mc.example", SlpPort = 25565 },
        });
        T.CheckEq("填了名字就用名字", "轻蓝", QlServerStatus.BuildDefaultProbes()[0].DisplayLabel);
    }

    private static void AuthFallback()
    {
        QlDeployment.Reset();
        var empty = QlDeployment.AuthEndpoints;
        T.CheckEq("空配置回落官方 devicecode", "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode",
            empty.DeviceCodeUrl);
        T.CheckEq("空配置回落官方 token", "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            empty.TokenUrl);
        T.CheckEq("空配置回落官方 XBL", "https://user.auth.xboxlive.com/user/authenticate", empty.XblAuthUrl);
        T.CheckEq("空配置回落官方 MC 登录",
            "https://api.minecraftservices.com/authentication/login_with_xbox", empty.McLoginUrl);

        QlDeployment.Set(new QlDeploymentConfig
        {
            Auth = new QlAuthDeployment
            {
                DeviceCodeUrl = " https://auth.example/device ",
                McProfileUrl = "https://auth.example/profile",
            },
        });
        var mixed = QlDeployment.AuthEndpoints;
        T.CheckEq("自定义端点生效", "https://auth.example/device", mixed.DeviceCodeUrl);
        T.CheckEq("未填端点仍回落官方", "https://login.microsoftonline.com/consumers/oauth2/v2.0/token",
            mixed.TokenUrl);
        T.CheckEq("自定义 profile 生效", "https://auth.example/profile", mixed.McProfileUrl);
    }

    private static void ParseConfig()
    {
        const string json = """
            {"maxMemoryMb":4096,
             "deployment":{
               "server":{"name":"甲","slpHost":"mc.example","slpPort":25566},
               "mtr":{"baseUrl":"https://mtr.example/"},
               "sync":{"publisherBaseUrl":"https://files.example/"},
               "logReport":{"enabled":false,"url":""}}}
            """;
        var cfg = JsonSerializer.Deserialize<QlConfig>(json)!;
        T.Check("deployment 段被解析", cfg.Deployment is not null);
        QlDeployment.Load(cfg);
        T.Check("解析后 SLP 就绪", QlDeployment.HasServer);
        T.CheckEq("解析后 MTR 地址归一", "https://mtr.example", QlDeployment.MtrBaseUrl);

        var missing = JsonSerializer.Deserialize<QlConfig>("""{"maxMemoryMb":2048}""")!;
        QlDeployment.Load(missing);
        T.Check("缺 deployment 段按全空处理",
            !QlDeployment.HasServer && !QlDeployment.HasMtr && !QlDeployment.HasForum);

        var nulls = JsonSerializer.Deserialize<QlConfig>(
            """{"deployment":{"server":null,"mtr":null,"auth":null}}""")!;
        QlDeployment.Load(nulls);
        T.Check("显式 null 子段不炸", !QlDeployment.HasServer && QlDeployment.AuthEndpoints.TokenUrl.Length > 0);
    }

    private static void EmbeddedFallback()
    {
        var embedded = new QlDeploymentConfig
        {
            Server = new QlServerDeployment { Name = "内嵌服", SlpHost = "mc.embedded", SlpPort = 25565 },
            Mtr = new QlEndpointDeployment { BaseUrl = "https://mtr.embedded" },
        };

        QlDeployment.Load(new QlConfig(), embedded);
        T.Check("无外部 deployment 时用内嵌", QlDeployment.HasServer && QlDeployment.HasMtr);
        T.CheckEq("内嵌服务器名生效", "内嵌服", QlDeployment.ServerName);

        var external = new QlConfig
        {
            Deployment = new QlDeploymentConfig
            {
                Forum = new QlEndpointDeployment { BaseUrl = "https://forum.external" },
            },
        };
        QlDeployment.Load(external, embedded);
        T.Check("外部 deployment 整段覆盖内嵌", !QlDeployment.HasServer && !QlDeployment.HasMtr);
        T.CheckEq("外部论坛地址生效", "https://forum.external", QlDeployment.ForumBaseUrl);

        QlDeployment.Load(new QlConfig(), null);
        T.Check("没有内嵌兜底时保持全空", !QlDeployment.HasServer && !QlDeployment.HasMtr);
    }
}
