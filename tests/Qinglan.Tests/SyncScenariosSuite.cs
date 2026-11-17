using System.Text;
using Qinglan.Launcher.Core;

namespace Qinglan.Tests;

/// <summary>E2E：包驱动引擎（QL-HMCL 新版方式）。全部内容内嵌 overrides 的 FULL 包为主，
/// 部分失败场景用 index-only 的 LITE 包 + fixture 服务器。</summary>
internal static class SyncScenariosSuite
{
    /// <summary>FULL 包载荷：std mods（含可选组 voxy）+ common（config/resourcepacks/shaderpacks）。</summary>
    private static (string Path, string Content)[] FullOverrides() => new[]
    {
        ("mods/A.jar", "A_CONTENT"),
        ("mods/B_voxy.jar", "B_CONTENT"),
        ("mods/C.jar", "C_CONTENT"),
        ("config/c1.toml", "c1"),
        ("resourcepacks/r1.zip", "r1"),
        ("shaderpacks/s1.zip", "s1"),
    };

    public static void Run()
    {
        var dir = Fx.TempDir();
        using var sink = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult(); // FULL 包用不到网络；防意外请求
        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "full.mrpack"), sink,
            new List<(string, string)>(), FullOverrides());

        T.Group("场景 A：FULL 包全新应用（overrides 直写，零下载）");
        var a = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("A downloaded=6（std3+common3）", 6, a.Downloaded);
        T.CheckEq("A unchanged=0 renamed=0 deleted=0 handedOver=0 failed=0",
            (0, 0, 0, 0, 0), (a.Unchanged, a.Renamed, a.Deleted, a.HandedOver, a.Failed));
        VerifyFreshLayout(dir);
        var stateA = QlSyncState.Load(dir);
        T.Check("A 状态版本 = Qinglan-Test 1.0（-full 已剥）",
            stateA.PackVersion == "Qinglan-Test 1.0");
        T.Check("A optionalRecorded 落地", stateA.OptionalRecorded);
        T.Check("A 可选组 voxy 随包启用", stateA.IsOptionalEnabled("voxy"));
        T.Check("A 活跃频道恒 std", stateA.ActiveChannel == "std");
        T.Check("A packUrl 未记录（未传）", stateA.PackUrl == null);
        T.Check("A tracked std=3 common=3（单频道）",
            (3, 3) == (stateA.TrackedOf("std").Count, stateA.TrackedOf("common").Count));

        T.Group("场景 B：幂等重跑");
        var b = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("B unchanged=6", 6, b.Unchanged);
        T.CheckEq("B downloaded/renamed/deleted=0", (0, 0, 0), (b.Downloaded, b.Renamed, b.Deleted));

        T.Group("场景 C：停用 voxy（单频道改名 1 次，免下载）");
        var stateC = QlSyncState.Load(dir);
        stateC.SetOptionalEnabled("voxy", false);
        stateC.Save(dir);
        var c = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("C renamed=1", 1, c.Renamed);
        T.CheckEq("C downloaded=0 deleted=0", (0, 0), (c.Downloaded, c.Deleted));
        T.Check("C 文件停成禁用名", File.Exists(Path.Combine(dir, "mods", "B_voxy.jar.disabled")) &&
            !File.Exists(Path.Combine(dir, "mods", "B_voxy.jar")));
        // Java 同款 fallback（QLPackSync: enabledGroups.isEmpty() ? allGroups : enabledGroups）：
        // 停用唯一可选组时 enabledGroups 为空，记录回落随包状态——下一轮会按启用计划
        // 从 .disabled 姊妹收养还原。文件此刻仍是停用态，由上面的禁用名断言钉住。
        T.Check("C 记录回落随包状态（停用全组 → Java fallback）",
            QlSyncState.Load(dir).IsOptionalEnabled("voxy"));
        DisableSomeScenario();

        T.Group("场景 D：还原 voxy（姊妹件收养，免下载）");
        var stateD = QlSyncState.Load(dir);
        stateD.SetOptionalEnabled("voxy", true);
        stateD.Save(dir);
        var d = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("D renamed=1（收养 disabled 姊妹）", 1, d.Renamed);
        T.Check("D 文件回到原名", File.Exists(Path.Combine(dir, "mods", "B_voxy.jar")) &&
            !File.Exists(Path.Combine(dir, "mods", "B_voxy.jar.disabled")));

        T.Group("场景 E：packUrl 记录语义（null 保留 / 空 = 清除 / 非空 = 记录）");
        QlPackSync.Sync(dir, mrpack, packUrl: "https://example.invalid/p.mrpack");
        T.Check("E 非空 packUrl 记录",
            QlSyncState.Load(dir).PackUrl == "https://example.invalid/p.mrpack");
        QlPackSync.Sync(dir, mrpack, packUrl: "  ");
        T.Check("E 空白 packUrl 归一成 null", QlSyncState.Load(dir).PackUrl == null);
        QlPackSync.Sync(dir, mrpack, packUrl: "https://example.invalid/p.mrpack");
        QlPackSync.Sync(dir, mrpack, packUrl: null);
        T.Check("E null packUrl 保留已记录值",
            QlSyncState.Load(dir).PackUrl == "https://example.invalid/p.mrpack");

        T.Group("场景 F：进度回调计数");
        int lastFinished = 0, lastTotal = 0;
        QlPackSync.Sync(dir, mrpack, listener: (f, t) => { lastFinished = f; lastTotal = t; });
        // Java 语义：total = planned + tracked（仍在计划内的旧追踪条目被双计），
        // 清理循环遇到 plannedPaths 内的路径直接 continue、不推进 finished——
        // 幂等轮 finished 停在计划数（6）上，total = 6 计划 + 6 tracked = 12。
        T.Check("进度按 Java 语义：幂等轮 finished=6/total=12",
            lastFinished == 6 && lastTotal == 12);

        PurgeScenario();
        RpuHandoverScenario();
        PartialFailureScenario();
    }

    private static void VerifyFreshLayout(string dir)
    {
        T.Check("mod 直落 mods/", File.Exists(Path.Combine(dir, "mods", "A.jar")));
        T.Check("config 落位", File.Exists(Path.Combine(dir, "config", "c1.toml")));
        // 用户口径：resourcepacks/shaderpacks 是 mrpack 管的，照常同步。
        T.Check("resourcepacks 照常同步", File.Exists(Path.Combine(dir, "resourcepacks", "r1.zip")));
        T.Check("shaderpacks 照常同步", File.Exists(Path.Combine(dir, "shaderpacks", "s1.zip")));
        T.Check("dblpack.json 已生成", File.Exists(Path.Combine(dir, "dblpack.json")));
    }

    /// <summary>v1 有、v2 没有的条目要被清掉；用户手放文件绝不触碰。</summary>
    private static void PurgeScenario()
    {
        var dir = Fx.TempDir();
        using var sink = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var v1 = Fx.BuildMrpack(Path.Combine(dir, "v1.mrpack"), sink,
            new List<(string, string)>(), new[]
            {
                ("mods/A.jar", "A_CONTENT"),
                ("mods/Old.jar", "OLD"),
            });
        var v2 = Fx.BuildMrpack(Path.Combine(dir, "v2.mrpack"), sink,
            new List<(string, string)>(), new[]
            {
                ("mods/A.jar", "A_CONTENT"),
            });

        QlPackSync.Sync(dir, v1);
        // 用户手放文件
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.WriteAllText(Path.Combine(dir, "mods", "Mine.jar"), "mine");

        var outcome = QlPackSync.Sync(dir, v2);
        T.CheckEq("purge deleted=1", 1, outcome.Deleted);
        T.Check("Old.jar 被删除", !File.Exists(Path.Combine(dir, "mods", "Old.jar")));
        T.Check("用户文件分毫未动", File.Exists(Path.Combine(dir, "mods", "Mine.jar")));
        T.Check("tracked 不再含 Old",
            !QlSyncState.Load(dir).TrackedOf("std").ContainsKey("mods/Old.jar"));
    }

    /// <summary>只停一部分组时 enabledGroups 非空、不触发 fallback：记录 = 精确启用集。</summary>
    private static void DisableSomeScenario()
    {
        var dir = Fx.TempDir();
        using var sink = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "two.mrpack"), sink,
            new List<(string, string)>(), new[]
            {
                ("mods/A.jar", "A_CONTENT"),
                ("mods/B_voxy.jar", "B_CONTENT"),
                // 文件名必须含 distant+horizon 才会进 dh 组（OptionalGroupOf 按名识别）。
                ("mods/DistantHorizons-2.1.jar", "D_CONTENT"),
            });

        QlPackSync.Sync(dir, mrpack);
        var state = QlSyncState.Load(dir);
        T.Check("夹具自检：两组都识别出来",
            state.IsOptionalEnabled("voxy") && state.IsOptionalEnabled("dh"));
        state.SetOptionalEnabled("dh", false);
        state.Save(dir);

        var outcome = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("只停一组：renamed=1（dh 停车）", 1, outcome.Renamed);
        T.CheckEq("只停一组：unchanged=2", 2, outcome.Unchanged);
        T.Check("dh 停成禁用名", File.Exists(Path.Combine(dir, "mods", "DistantHorizons-2.1.jar.disabled")) &&
            !File.Exists(Path.Combine(dir, "mods", "DistantHorizons-2.1.jar")));
        T.Check("voxy 照常原名在位", File.Exists(Path.Combine(dir, "mods", "B_voxy.jar")));
        var recorded = QlSyncState.Load(dir);
        T.Check("记录 = 精确启用集（voxy 在、dh 不在）",
            recorded.IsOptionalEnabled("voxy") && !recorded.IsOptionalEnabled("dh"));
    }

    /// <summary>qlrpu/ 与 config/qlrpu-client.json 归 RPU 客户端：清单不收，写入不碰，
    /// 旧追踪的遗留只交接不删除。</summary>
    private static void RpuHandoverScenario()
    {
        var dir = Fx.TempDir();
        using var sink = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "rpu.mrpack"), sink,
            new List<(string, string)>(), new[]
            {
                ("mods/A.jar", "A_CONTENT"),
                ("qlrpu/cache/current.json", "QCACHE"),
                ("config/qlrpu-client.json", "QCONF"),
            });

        var manifest = QlMrpackManifest.BuildFromMrpack(mrpack);
        var commonPaths = manifest.ChannelOrThrow(QlManifest.ChannelCommon).Files.Select(f => f.Path).ToList();
        T.Check("构建期就跳过 qlrpu/", !commonPaths.Any(p => p!.StartsWith("qlrpu/")));
        T.Check("构建期就跳过 config/qlrpu-client.json",
            !commonPaths.Contains("config/qlrpu-client.json"));

        QlPackSync.Sync(dir, mrpack);
        T.Check("RPU 独占路径绝不写入",
            !File.Exists(Path.Combine(dir, "qlrpu", "cache", "current.json")) &&
            !File.Exists(Path.Combine(dir, "config", "qlrpu-client.json")));

        // 模拟旧引擎的遗留：RPU 文件曾被追踪。
        var state = QlSyncState.Load(dir);
        Directory.CreateDirectory(Path.Combine(dir, "config")); // 引擎从不写 RPU 路径，目录未必在
        File.WriteAllText(Path.Combine(dir, "config", "qlrpu-client.json"), "QCONF");
        state.Track("common", "config/qlrpu-client.json", Fx.Sha1("QCONF"));
        state.Save(dir);

        var outcome = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("遗留 RPU 文件 = 交接而非删除", 1, outcome.HandedOver);
        T.Check("交接的文件原地保留", File.Exists(Path.Combine(dir, "config", "qlrpu-client.json")));
        T.CheckEq("无误删", 0, outcome.Deleted);
    }

    /// <summary>LITE 包：index-only 文件从 CDN 拉。404 文件进 failedPaths；
    /// 新版语义：状态照记（版本/已成功的 tracked 落地），修复后一轮补齐。</summary>
    private static void PartialFailureScenario()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        server.AddFile("/mods/A.jar", Encoding.UTF8.GetBytes("A_CONTENT"));
        server.AddFile("/mods/B.jar", Encoding.UTF8.GetBytes("B_CONTENT"));
        server.FailPath("/mods/B.jar");

        var mrpack = Fx.BuildMrpack(Path.Combine(dir, "lite.mrpack"), server,
            new List<(string, string)>
            {
                ("mods/A.jar", "A_CONTENT"),
                ("mods/B.jar", "B_CONTENT"),
            }, Array.Empty<(string, string)>());

        var bad = QlPackSync.Sync(dir, mrpack);
        T.Check("failedPaths 含 B.jar", bad.FailedPaths.Any(f => f.EndsWith("/B.jar")));
        T.CheckEq("失败计 1", 1, bad.Failed);
        T.CheckEq("A 照常下载", 1, bad.Downloaded);
        // 新版语义：状态照记（对齐 QLPackSync——失败只影响该文件的 tracked，不阻断保存）。
        var stateBad = QlSyncState.Load(dir);
        T.Check("失败轮版本照记（QL-HMCL 同款）",
            stateBad.PackVersion == "Qinglan-Test 1.0");
        T.Check("失败文件不进 tracked", !stateBad.TrackedOf("std").ContainsKey("mods/B.jar"));
        T.Check("成功文件进 tracked", stateBad.TrackedOf("std").ContainsKey("mods/A.jar"));

        server.RemoveFail("/mods/B.jar");
        var ok = QlPackSync.Sync(dir, mrpack);
        T.CheckEq("修复后补下 B.jar", 1, ok.Downloaded);
        T.Check("补齐后 tracked 齐了",
            QlSyncState.Load(dir).TrackedOf("std").ContainsKey("mods/B.jar"));
    }
}
