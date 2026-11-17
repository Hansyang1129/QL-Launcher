using System.Text;
using Qinglan.Launcher.Core;

namespace Qinglan.Tests;

/// <summary>mrpack→manifest 构建语义（路由/排除/RPU 跳过）、overrides 优先写入、
/// 姊妹件收养、dblpack 清单与包头部预览。</summary>
internal static class PackDblpackSuite
{
    public static void Run()
    {
        T.Group("mrpack→manifest 构建（路由/排除/RPU）");
        RoutingScenario();
        T.Group("overrides 优先写入与 index 下载");
        OverridesFirstScenario();
        IndexDownloadScenario();
        T.Group("dblpack 清单与包头部预览");
        DblpackMetadata();
        MrpackHeadScenario();
    }

    /// <summary>buildFromMrpack 的路由表：mods→std；config/shaderpacks/resourcepacks + options.txt→common；
    /// PCL、servers.dat、modrinth.index.json、fabric-loader-/fabric-api- 排除；RPU 独占路径不发布；
    /// index 与 overrides 同路径时 index 赢（coveredByIndex）。</summary>
    private static void RoutingScenario()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();

        var indexFiles = new List<(string, string)>
        {
            ("mods/Indexed.jar", "INDEXED"),
            ("mods/Dup.jar", "DUP_CONTENT"), // overrides 里再出现 → index 赢
        };
        var overrideFiles = new[]
        {
            ("mods/Dup.jar", "DUP_CONTENT"),
            ("mods/fabric-api-0.9.jar", "EXCLUDE_ME"),
            ("mods/fabric-loader-0.16.jar", "EXCLUDE_ME"),
            ("config/create-server.toml", "cfg"),
            ("options.txt", "opts"),
            ("shaderpacks/s.zip", "shader"),
            ("resourcepacks/r.zip", "rpack"),
            ("qlrpu/cache/current.json", "QCACHE"),
            ("config/qlrpu-client.json", "QCONF"),
            ("PCL/log.txt", "PCL_LOG"),
            ("servers.dat", "SERVERS"),
            ("schematics/x.litematic", "UNROUTED"),
        };
        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "route.mrpack"), server, indexFiles, overrideFiles);

        var m = QlMrpackManifest.BuildFromMrpack(mrpack);
        var std = m.ChannelOrThrow(QlManifest.ChannelMods).Mods.Select(f => f.Path).ToList();
        var common = m.ChannelOrThrow(QlManifest.ChannelCommon).Files.Select(f => f.Path).ToList();

        T.Check("std 收编 mods", std.Contains("mods/Indexed.jar") && std.Contains("mods/Dup.jar"));
        T.Check("排除 fabric-api/loader", !std.Any(p => p!.Contains("fabric-")));
        T.Check("common 收编 config/options/shader/resourcepack",
            common.Contains("config/create-server.toml") && common.Contains("options.txt") &&
            common.Contains("shaderpacks/s.zip") && common.Contains("resourcepacks/r.zip"));
        T.Check("RPU 独占路径不发布",
            !common.Contains("qlrpu/cache/current.json") && !common.Contains("config/qlrpu-client.json"));
        T.Check("PCL/servers.dat/modrinth.index.json 跳过",
            !common.Contains("PCL/log.txt") && !common.Contains("servers.dat") &&
            !common.Any(p => p!.StartsWith("modrinth.index.json")));
        T.Check("未路由路径不发布", !common.Contains("schematics/x.litematic"));

        // index 赢：Dup.jar 的 urls 应带服务器 CDN 地址（index 条目），overrides 载荷同路径被覆盖。
        var dup = m.ChannelOrThrow(QlManifest.ChannelMods).Mods.First(f => f.Path == "mods/Dup.jar");
        T.Check("index 条目的 CDN 地址在前", dup.Urls[0].StartsWith(server.BaseUrl));

        T.Check("包名/versionId 来自 index",
            m.Pack!.Name == "Qinglan Testseed" && m.Pack.Version == "Qinglan-Test 1.0");

        // 文件名识别可选组：只有 mod 进组，config 不进。
        var voxyMrpack = Fx.BuildMrpack(Path.Combine(dir, "groups.mrpack"), server,
            new List<(string, string)>(), new[]
            {
                ("mods/voxy-1.9.jar", "V"),
                ("mods/DistantHorizons-2.1.jar", "DH"),
                ("mods/axiom-1.0.jar", "AX"),
                ("config/DistantHorizons.toml", "DHC"),
            });
        var mg = QlMrpackManifest.BuildFromMrpack(voxyMrpack);
        var mods = mg.ChannelOrThrow(QlManifest.ChannelMods).Mods;
        T.Check("voxy 进组", mods.First(f => f.Path == "mods/voxy-1.9.jar").Optional == "voxy");
        T.Check("DistantHorizons 组名 dh",
            mods.First(f => f.Path == "mods/DistantHorizons-2.1.jar").Optional == "dh");
        T.Check("axiom 进组", mods.First(f => f.Path == "mods/axiom-1.0.jar").Optional == "axiom");
        T.Check("可选运行时的 config 不进组",
            mg.ChannelOrThrow(QlManifest.ChannelCommon).Files
                .First(f => f.Path == "config/DistantHorizons.toml").Optional == null);
        // overrides 按条目名排序遍历（Java 工具同序），组顺序跟随排序后的文件名：
        // DistantHorizons < axiom < voxy（ASCII 序，大写在前）。
        T.Check("QlPackSync.OptionalGroups 顺序 = 条目名排序序",
            QlPackSync.OptionalGroups(voxyMrpack).SequenceEqual(new[] { "dh", "axiom", "voxy" }));
    }

    /// <summary>overrides 载荷优先：文件已存在但内容陈旧 → 直接从 zip 内嵌载荷覆写，零网络。</summary>
    private static void OverridesFirstScenario()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "full.mrpack"), server,
            new List<(string, string)>(), new[] { ("mods/A.jar", "A_CONTENT") });

        // 预置一个内容陈旧的目标文件（上一版包的遗留）。
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.WriteAllText(Path.Combine(dir, "mods", "A.jar"), "STALE");
        // 服务器上没有这个文件：走网络必失败，从而证明写入用的是内嵌载荷。

        var outcome = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("陈旧文件从载荷覆写", 1, outcome.Downloaded);
        T.Check("内容已是包内版本",
            File.ReadAllText(Path.Combine(dir, "mods", "A.jar")) == "A_CONTENT");
        T.CheckEq("全程零网络命中", 0, server.Hits("/mods/A.jar"));
    }

    /// <summary>LITE 包：index-only 文件走 CDN 下载 + SHA-1 校验落位。</summary>
    private static void IndexDownloadScenario()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        server.AddFile("/mods/A.jar", Encoding.UTF8.GetBytes("A_CONTENT"));
        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "lite.mrpack"), server,
            new List<(string, string)> { ("mods/A.jar", "A_CONTENT") },
            Array.Empty<(string, string)>());

        var outcome = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("index 文件下载落位", 1, outcome.Downloaded);
        T.Check("内容校验通过",
            File.ReadAllText(Path.Combine(dir, "mods", "A.jar")) == "A_CONTENT");
    }

    /// <summary>fabric.mod.json 让 dblpack 携带 mod 名与版本；资源包按文件名解析版本。</summary>
    private static void DblpackMetadata()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();

        var bytes = Fx.ModWithFabricJson("Cool Mod", "3.2");
        server.AddFile("/mods/Fabric.jar", bytes);
        server.AddFile("/mods/Cool.jar", Encoding.UTF8.GetBytes("PLAIN_CONTENT"));
        server.AddFile("/resourcepacks/Faithful_32x_v1.2.1.zip", Encoding.UTF8.GetBytes("RP"));

        var mrpack = Fx.BuildMrpackRaw(Path.Combine(dir, "meta.mrpack"), server,
            new List<(string, byte[])>
            {
                ("mods/Fabric.jar", bytes),
                ("mods/Cool.jar", Encoding.UTF8.GetBytes("PLAIN_CONTENT")),
                ("resourcepacks/Faithful_32x_v1.2.1.zip", Encoding.UTF8.GetBytes("RP")),
            }, Array.Empty<(string, byte[])>());

        QlPackSync.Sync(dir, mrpack);

        var dbl = QlDblPack.Load(dir)!;
        var fabricMod = dbl.Mods.First(m => m.Path == "mods/Fabric.jar");
        T.Check("fabric.mod.json 名字被解析", fabricMod.Name == "Cool Mod");
        T.Check("fabric.mod.json 版本被解析", fabricMod.Version == "3.2");
        T.Check("无内嵌信息的条目不给名字", dbl.Mods.First(m => m.Path == "mods/Cool.jar").Name == null);
        T.Check("资源包条目收进 dblpack", dbl.Resourcepacks.Any(r => r.Path == "resourcepacks/Faithful_32x_v1.2.1.zip"));
        T.Check("资源包版本按文件名解析",
            dbl.Resourcepacks.First(r => r.Path!.EndsWith("Faithful_32x_v1.2.1.zip")).Version == "1.2.1");
    }

    /// <summary>ReadHead 预览 + 版本归一；缺 index 的包：ReadHead 报清晰错误，
    /// 构建器按 FULL 包容忍（只发 overrides）。</summary>
    private static void MrpackHeadScenario()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var pack = Fx.BuildMrpack(Path.Combine(dir, "head.mrpack"), server,
            new List<(string, string)>(), new[] { ("mods/A.jar", "A_CONTENT") });

        var head = QlMrpack.ReadHead(pack);
        T.Check("头部名字来自 index", head.Name == "Qinglan Testseed");
        T.Check("头部 versionId 原样", head.VersionId == "Qinglan-Test 1.0-full");
        T.Check("目标游戏 = minecraft", head.Game == "minecraft");
        T.Check("依赖齐备", head.Minecraft == "1.20.1" && head.FabricLoader == "0.16.9");
        T.Check("NormalizeVersionId 剥 -full", QlMrpack.NormalizeVersionId("Qinglan-Test 1.0-full") == "Qinglan-Test 1.0");
        T.Check("ReadPackVersion 已归一", QlPackSync.ReadPackVersion(pack) == "Qinglan-Test 1.0");

        var bad = Path.Combine(dir, "bad.mrpack");
        using (var fs = File.Create(bad))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            zip.CreateEntry("random.txt");
        try
        {
            QlMrpack.ReadHead(bad);
            T.Check("缺 index 的 mrpack ReadHead 被拒", false);
        }
        catch (IOException e)
        {
            T.Check("缺 index 的 mrpack ReadHead 被拒", e.Message.Contains("modrinth.index.json"));
        }
        var m = QlMrpackManifest.BuildFromMrpack(bad);
        T.Check("构建器容忍缺 index（FULL 语义：只有 overrides）",
            m.ChannelOrThrow(QlManifest.ChannelMods).Mods.Count == 0 &&
            m.ChannelOrThrow(QlManifest.ChannelCommon).Files.Count == 0);
        T.Check("容忍缺 index 时包名回落默认", m.Pack!.Name == "Qinglan-2nd" && m.Pack.Version == "26.8.1");
    }
}
