namespace Qinglan.Launcher.Core;

/// <summary>裁决一个实例能否使用轻蓝同步页。</summary>
/// <para>同步页不是「随便更新这个实例」的按钮：只有携带整合包的 <b>RPU mod</b>
/// （游戏内资源包更新器，例如 <c>[C]QingLanRPUClient-1.0.0-updated.jar</c>）的实例
/// 才属于社区包、才能由启动器更新。其余都是个人实例，绝不动——
/// 把包的 mod 灌进别人自己的存档环境是破坏性的。</para>
public static class QlPackGuard
{
    /// <summary>包更新器 mod 文件名必须携带的标记（大小写不敏感）：
    /// <c>[C]QingLanRPUClient-1.0.0-updated.jar</c>。</summary>
    /// <para>刻意收窄：名字里碰巧带 rpu 的 mod 绝不允许解锁同步页。</para>
    public const string RpuModMarker = "qinglanrpuclient";

    /// <summary>在实例的 mods 目录里找 RPU mod。</summary>
    /// <returns>启用中的 RPU mod 文件名；实例不携带（或唯一副本已停成
    /// <c>*.jar.disabled</c>）时为 <see langword="null"/>。</returns>
    public static string? FindRpuMod(string gameDir)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(mods)) return null;
        try
        {
            return Directory.EnumerateFiles(mods)
                .Where(File.Exists)
                .Select(Path.GetFileName)
                .Where(name =>
                {
                    var lower = name!.ToLowerInvariant();
                    // 停用的副本不算：包更新器没在跑。
                    return lower.EndsWith(".jar", StringComparison.Ordinal)
                           && lower.Contains(RpuModMarker, StringComparison.Ordinal);
                })
                .OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>同步页是否允许更新该实例。</summary>
    public static bool IsSyncable(string gameDir) => FindRpuMod(gameDir) != null;
}
