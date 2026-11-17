using Qinglan.Launcher.Core;

namespace Qinglan.Tests;

/// <summary>目录布局：标记读写、recover 分支；新版只剩单一 mods 频道（wire 名 std），
/// 历史 pfm 遗迹按「无效标记、不抬升」处理。</summary>
internal static class LayoutSuite
{
    public static void Run()
    {
        T.Group("频道标记");
        MarkerRoundtrip();
        BadMarkerIsNull();
        LegacyPfmMarkerIsNull();

        T.Group("recover 分支");
        RecoverHealsSimple();
        RecoverTrustsDiskMarker();
        RecoverPromotesParked();
        RecoverIgnoresLegacyPfmPark();
        RecoverCreatesWhenNothing();
    }

    private static string FreshLayout(out string gameDir)
    {
        gameDir = Fx.TempDir();
        Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
        return gameDir;
    }

    private static void MarkerRoundtrip()
    {
        var dir = Fx.TempDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        QlDirectoryLayout.StampChannel(mods, "std");
        T.Check("标记写出可读", QlDirectoryLayout.ReadChannelMarker(mods) == "std");
        T.Check("识别活跃频道 fallback 命中", QlDirectoryLayout.DetectActiveChannel(dir, "std") == "std");
    }

    private static void BadMarkerIsNull()
    {
        var dir = Fx.TempDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, QlDirectoryLayout.ChannelMarker), "nonsense");
        T.Check("乱值标记视为无标记", QlDirectoryLayout.ReadChannelMarker(mods) == null);
    }

    private static void LegacyPfmMarkerIsNull()
    {
        var dir = Fx.TempDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        QlDirectoryLayout.StampChannel(mods, "pfm");
        // 新版只有一个 mods 频道：pfm 不再是合法标记值。
        T.Check("pfm 标记失效（单频道时代）", QlDirectoryLayout.ReadChannelMarker(mods) == null);
    }

    private static void RecoverHealsSimple()
    {
        FreshLayout(out var dir);
        Directory.CreateDirectory(Path.Combine(dir, "mods"));
        File.WriteAllText(Path.Combine(dir, "mods", "keep.txt"), "x");
        // mods 只存在没标记；没有 parked folder → 补标记即收工
        QlDirectoryLayout.Recover(dir, "std");
        T.Check("recover 给 mods 盖上 std 标记",
            QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods")) == "std");
        T.Check("recover 不删既有内容", File.Exists(Path.Combine(dir, "mods", "keep.txt")));
    }

    private static void RecoverTrustsDiskMarker()
    {
        var dir = Fx.TempDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        QlDirectoryLayout.StampChannel(mods, "std");
        Directory.CreateDirectory(Path.Combine(dir, "mods-std"));
        QlDirectoryLayout.StampChannel(Path.Combine(dir, "mods-std"), "std");
        // 记录是历史遗留值（pfm 已不合法，落回 std），磁盘盖的是 std：磁盘赢。
        QlDirectoryLayout.Recover(dir, "pfm");
        T.Check("磁盘标记赢过记录", QlDirectoryLayout.ReadChannelMarker(mods) == "std");
    }

    private static void RecoverPromotesParked()
    {
        var dir = Fx.TempDir();
        // mods 没了；mods-std 留着并有标记 → 抬升回去
        Directory.CreateDirectory(Path.Combine(dir, "mods-std"));
        QlDirectoryLayout.StampChannel(Path.Combine(dir, "mods-std"), "std");
        QlDirectoryLayout.Recover(dir, "std");
        T.Check("被抬升为 active", Directory.Exists(Path.Combine(dir, "mods")) &&
            !Directory.Exists(Path.Combine(dir, "mods-std")) &&
            QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods")) == "std");
    }

    private static void RecoverIgnoresLegacyPfmPark()
    {
        var dir = Fx.TempDir();
        // 双频道时代的遗迹：mods 没了，mods-pfm 带着已失效的 pfm 标记。
        // 新版不再有 pfm 频道：既不抬升它，也绝不把它当内容源。
        Directory.CreateDirectory(Path.Combine(dir, "mods-pfm"));
        QlDirectoryLayout.StampChannel(Path.Combine(dir, "mods-pfm"), "pfm");
        File.WriteAllText(Path.Combine(dir, "mods-pfm", "legacy.jar"), "old");
        QlDirectoryLayout.Recover(dir, "pfm");
        T.Check("pfm 遗迹不被抬升：mods 新建",
            Directory.Exists(Path.Combine(dir, "mods")));
        T.Check("pfm 遗迹不被抬升：mods 盖 std 标记",
            QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods")) == "std");
        T.Check("pfm 遗迹原地保留（不删）", Directory.Exists(Path.Combine(dir, "mods-pfm")) &&
            File.Exists(Path.Combine(dir, "mods-pfm", "legacy.jar")));
    }

    private static void RecoverCreatesWhenNothing()
    {
        var dir = Fx.TempDir();
        QlDirectoryLayout.Recover(dir, "std");
        T.Check("空目录里直接建 mods 并盖章",
            Directory.Exists(Path.Combine(dir, "mods")) &&
            QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods")) == "std");
    }
}
