using System.Text;
using Qinglan.Launcher.Core;

namespace Qinglan.Tests;

/// <summary>E2E：复刻 HMCL-QL 的五场景 + purge / 部分失败 / 取消。全部走本地 fixture 服务器。</summary>
internal static class SyncScenariosSuite
{
    // std/pfm 共享同一内容（模拟"切频道只换布局不重下"），dh 是可选组。
    private static Fx.ManifestData Data() => Fx.Manifest(
        new[]
        {
            ("mods/A.jar", "A_CONTENT", (string?)null),
            ("mods/B.jar", "B_CONTENT", "dh"),
            ("mods/C.jar", "C_CONTENT", (string?)null),
        },
        common: new[]
        {
            ("config/c1.toml", "c1"),
            ("resourcepacks/r1.zip", "r1"),
        });

    private static Fx.ManifestData? _cachedData;
    private static Fx.ManifestData CachedData() => _cachedData ??= Data();

    public static void Run()
    {
        var data = Data();
        var dir = Fx.TempDir();
        using (var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult())
        {
            var json = Fx.WithBaseUrl(data.Json, server.BaseUrl);
            var manifest = QinglanJsonParse(json);

            T.Group("场景 A：新装 std + dh");
            RegisterContents(server, data);
            var a = QlSyncManager.Sync(dir, manifest, "std", new Dictionary<string, bool> { ["dh"] = true });
            T.CheckEq("A 比 downloaded=8（std3+pfm3+common2）", 8, a.Downloaded);
            T.CheckEq("A unchanged=0", 0, a.Unchanged);
            T.CheckEq("A renamed=0 deleted=0", (0, 0), (a.Renamed, a.Deleted));
            T.Check("A 无切频道", !a.ChannelSwitched);
            T.Check("A 状态版本", a.PackVersion == "1.0.0-test");
            VerifyLayout(dir, "std", enableDh: true);

            T.Group("场景 B：幂等重跑");
            var b = QlSyncManager.Sync(dir, manifest, "std");
            T.CheckEq("B unchanged=8（RENAME 不计入，且本轮无 rename）", 8, b.Unchanged);
            T.CheckEq("B downloaded/renamed/deleted=0", (0, 0, 0), (b.Downloaded, b.Renamed, b.Deleted));

            T.Group("场景 C：关 dh");
            var c = QlSyncManager.Sync(dir, manifest, "std", new Dictionary<string, bool> { ["dh"] = false });
            T.CheckEq("C renamed=2（std+pfm 各改一次名）", 2, c.Renamed);
            T.CheckEq("C downloaded=0", 0, c.Downloaded);
            T.Check("C 文件落成禁用名", File.Exists(Path.Combine(dir, "mods", "B.jar.disabled")) &&
                !File.Exists(Path.Combine(dir, "mods", "B.jar")));
            T.Check("C 用户不再启用 dh", !QlSyncState.Load(dir).IsOptionalEnabled("dh"));

            T.Group("场景 D：切 pfm");
            var d = QlSyncManager.Sync(dir, manifest, "pfm");
            T.Check("D 频道已切", d.ChannelSwitched);
            T.CheckEq("D downloaded=0（同内容零下载）", 0, d.Downloaded);
            T.CheckEq("D renamed=0 deleted=0", (0, 0), (d.Renamed, d.Deleted));
            VerifyLayout(dir, "pfm", enableDh: false);

            T.Group("场景 E：切回 std + dh");
            var e = QlSyncManager.Sync(dir, manifest, "std", new Dictionary<string, bool> { ["dh"] = true });
            T.Check("E 频道已切", e.ChannelSwitched);
            T.CheckEq("E renamed=2（std+pfm 各改一次名）", 2, e.Renamed);
            T.CheckEq("E downloaded=0 unchanged=6", (0, 6), (e.Downloaded, e.Unchanged));
            VerifyLayout(dir, "std", enableDh: true);
        }

        PurgeScenario();
        PartialFailureScenario();
        CancelScenario();
        AnalyzeScenario();
    }

    /// <summary>把 manifest 的全部内容注册进 fixture 服务器（URL 形如 /mods/X.jar）。</summary>
    private static void RegisterContents(MiniHttpServer server, Fx.ManifestData data)
    {
        foreach (var (path, content) in data.Contents)
            server.AddFile("/" + path, Encoding.UTF8.GetBytes(content));
    }

    private static QlManifest QinglanJsonParse(string json)
        => QlManifest.FromJson(json);

    private static void VerifyLayout(string dir, string active, bool enableDh)
    {
        var marker = QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods"));
        T.Check("活跃标记 = " + active, marker == active);
        var state = QlSyncState.Load(dir);
        T.Check("状态频道 = " + active, state.ActiveChannel == active);
        T.Check("dh 状态 = " + enableDh, state.IsOptionalEnabled("dh") == enableDh);

        var modsDir = Path.Combine(dir, "mods");
        T.Check("A.jar 在活跃 folder", File.Exists(Path.Combine(modsDir, "A.jar")));
        if (enableDh)
            T.Check("B.jar 启用中", File.Exists(Path.Combine(modsDir, "B.jar")));
        else
            T.Check("B.jar 禁用中", File.Exists(Path.Combine(modsDir, "B.jar.disabled")));
        T.Check("common 落在游戏目录", File.Exists(Path.Combine(dir, "config", "c1.toml")) &&
            File.Exists(Path.Combine(dir, "resourcepacks", "r1.zip")));
        T.Check("dblpack.json 已生成", File.Exists(Path.Combine(dir, "dblpack.json")));
        var trackedStd = state.TrackedOf("std");
        T.CheckEq("tracked std=3 common=2 pfm=3",
            (3, 2, 3), (trackedStd.Count, state.TrackedOf("common").Count, state.TrackedOf("pfm").Count));
    }

    /// <summary>v1 有、v2 没有的条目要被清掉；用户手放文件绝不触碰。</summary>
    private static void PurgeScenario()
    {
        var withExtra = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A_CONTENT", (string?)null),
            ("mods/Old.jar", "OLD", (string?)null),
        });
        var withOutExtra = Fx.Manifest(new[] { ("mods/A.jar", "A_CONTENT", (string?)null) });
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        RegisterContents(server, withExtra);

        var m1 = QlManifest.FromJson(Fx.WithBaseUrl(withExtra.Json, server.BaseUrl));
        QlSyncManager.Sync(dir, m1, "std");

        // 用户手放文件
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.WriteAllText(Path.Combine(dir, "mods", "Mine.jar"), "mine");

        var m2 = QlManifest.FromJson(Fx.WithBaseUrl(withOutExtra.Json, server.BaseUrl));
        var outcome = QlSyncManager.Sync(dir, m2, "std");
        T.CheckEq("purge deleted=2（std+pfm 各删一个）", 2, outcome.Deleted);
        T.Check("Old.jar 被删除", !File.Exists(Path.Combine(dir, "mods", "Old.jar")));
        T.Check("用户文件分毫未动", File.Exists(Path.Combine(dir, "mods", "Mine.jar")));
        T.Check("tracked 不再含 Old", !QlSyncState.Load(dir).TrackedOf("std").ContainsKey("mods/Old.jar"));
    }

    /// <summary>404 文件：failedFiles 有账，记账（tracked/version）不落地。</summary>
    private static void PartialFailureScenario()
    {
        var data = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A_CONTENT", (string?)null),
            ("mods/B.jar", "B_CONTENT", (string?)null),
        }, pfm: Array.Empty<(string Path, string Content, string? Group)>());
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        RegisterContents(server, data);
        server.FailPath("/mods/B.jar");

        var m = QlManifest.FromJson(Fx.WithBaseUrl(data.Json, server.BaseUrl));
        var outcome = QlSyncManager.Sync(dir, m, "std");
        T.Check("failedFiles 含 B.jar", outcome.FailedFiles.Any(f => f.EndsWith("/B.jar")));
        T.CheckEq("失败轮不更新版本", null, QlSyncState.Load(dir).PackVersion!);

        // 修复后重跑闭环
        server.RemoveFail("/mods/B.jar");
        var ok = QlSyncManager.Sync(dir, m, "std");
        T.CheckEq("修复后补下 B.jar", 1, ok.Downloaded);
        T.Check("版本记账恢复", QlSyncState.Load(dir).PackVersion == "1.0.0-test");
    }

    /// <summary>取消：下载中途掐断 → cancelled=true，记账不落地。</summary>
    private static void CancelScenario()
    {
        var data = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A_CONTENT", (string?)null),
            ("mods/B.jar", "B_CONTENT", (string?)null),
        }, pfm: Array.Empty<(string Path, string Content, string? Group)>());
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        RegisterContents(server, data);
        server.DelayPath("/mods/B.jar");

        var m = QlManifest.FromJson(Fx.WithBaseUrl(data.Json, server.BaseUrl));
        using var cts = new CancellationTokenSource();
        var task = Task.Run(() => QlSyncManager.Sync(dir, m, "std", ct: cts.Token));
        cts.CancelAfter(400);
        var outcome = task.GetAwaiter().GetResult();
        T.Check("取消反馈 cancelled", outcome.Canceled);
        T.Check("取消后不记账", QlSyncState.Load(dir).PackVersion == null);
    }

    /// <summary>Analyze 干跑：计数与 sync 结果对齐，但不写盘。</summary>
    private static void AnalyzeScenario()
    {
        var data = Data();
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var m = QlManifest.FromJson(Fx.WithBaseUrl(data.Json, server.BaseUrl));

        var before = QlSyncManager.Analyze(dir, m, "std", new Dictionary<string, bool> { ["dh"] = true });
        T.Check("干跑（新装）需要 8 个下载", before.NeedDownload.Count == 8);
        T.Check("干跑不落盘", !File.Exists(Path.Combine(dir, "mods", "A.jar")) &&
            !File.Exists(Path.Combine(dir, QlSyncState.StateFile)));

        RegisterContents(server, data);
        QlSyncManager.Sync(dir, m, "std", new Dictionary<string, bool> { ["dh"] = true });
        var after = QlSyncManager.Analyze(dir, m, "std");
        T.Check("干跑（装完）= idle", after.IsIdle && after.NeedDownload.Count == 0);
    }
}
