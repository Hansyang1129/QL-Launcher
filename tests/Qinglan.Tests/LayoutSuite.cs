using Qinglan.Launcher.Core;

namespace Qinglan.Tests;

/// <summary>目录布局：标记读写、换名、recover 分支、switch 语义。</summary>
internal static class LayoutSuite
{
    public static void Run()
    {
        T.Group("频道标记");
        MarkerRoundtrip();
        BadMarkerIsNull();

        T.Group("recover 四分支");
        RecoverHealsSimple();
        RecoverTrustsDiskMarker();
        RecoverPromotesParked();
        RecoverInterruptedSwitch();

        T.Group("switchChannel");
        SwitchRoundTrip();
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
        QlDirectoryLayout.StampChannel(mods, "pfm");
        T.Check("标记写出可读", QlDirectoryLayout.ReadChannelMarker(mods) == "pfm");
        T.Check("识别活跃频道 fallback 命中", QlDirectoryLayout.DetectActiveChannel(dir, "std") == "pfm");
    }

    private static void BadMarkerIsNull()
    {
        var dir = Fx.TempDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, QlDirectoryLayout.ChannelMarker), "nonsense");
        T.Check("乱值标记视为无标记", QlDirectoryLayout.ReadChannelMarker(mods) == null);
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
        // 记录 pfm 但磁盘盖的是 std：磁盘赢
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

    private static void RecoverInterruptedSwitch()
    {
        var dir = Fx.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "mods-pfm"));
        QlDirectoryLayout.StampChannel(Path.Combine(dir, "mods-pfm"), "pfm");
        // 第一次改名(记 std→pfm 的 middle)后 mods 已迁走，mods-pfm 带标记停在原地
        QlDirectoryLayout.Recover(dir, "pfm");
        T.Check("中断切换补第二次改名", Directory.Exists(Path.Combine(dir, "mods")) &&
            QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods")) == "pfm");
    }

    private static void SwitchRoundTrip()
    {
        var dir = Fx.TempDir();
        var mods = Path.Combine(dir, "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(mods, "m.jar"), "m");
        QlDirectoryLayout.StampChannel(mods, "std");

        QlDirectoryLayout.SwitchChannel(dir, "std", "pfm");
        T.Check("切换后 mods=pfm", QlDirectoryLayout.ReadChannelMarker(mods) == "pfm");
        T.Check("原 std 收进 mods-std", Directory.Exists(Path.Combine(dir, "mods-std")) &&
            QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods-std")) == "std");
        T.Check("内容跟随 folder 走", File.Exists(Path.Combine(dir, "mods-std", "m.jar")));

        QlDirectoryLayout.SwitchChannel(dir, "pfm", "std");
        T.Check("切回 std 后 mods 带 std 标记", QlDirectoryLayout.ReadChannelMarker(mods) == "std");
        T.Check("pfm 停回 mods-pfm", Directory.Exists(Path.Combine(dir, "mods-pfm")));
    }
}
