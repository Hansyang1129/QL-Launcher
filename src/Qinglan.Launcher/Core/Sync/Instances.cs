namespace Qinglan.Launcher.Core;

/// <summary>定位一个游戏根目录里唯一受管的轻蓝实例。</summary>
/// <para>启动器支持版本隔离：实例可以住在 <c>&lt;根&gt;\versions\&lt;id&gt;\</c> 里，
/// 也可以是平铺布局（根目录自身就是实例运行目录）。受管实例固定是
/// <see cref="ManagedInstanceId"/>——根目录里的其他实例一概不碰。</para>
public static class QlInstances
{
    /// <summary>启动器唯一管理的实例 id（版本隔离目录名）。</summary>
    public const string ManagedInstanceId = "Qinglan-2nd-RPU";

    /// <summary>解析受管实例的运行目录；根目录里找不到时为 <see langword="null"/>。</summary>
    /// <para>优先版本隔离形态 <c>&lt;根&gt;\versions\Qinglan-2nd-RPU</c>；
    /// 其次平铺兼容——根目录自身携带 mods/ 或 QL 状态标记（dblpack.json、
    /// ql-manifest-state.json）时按实例运行目录对待。</para>
    public static string? ResolveRunDir(string gameRoot)
    {
        var isolated = Path.Combine(gameRoot, "versions", ManagedInstanceId);
        if (Directory.Exists(isolated)) return isolated;

        if (Directory.Exists(Path.Combine(gameRoot, "mods"))
            || File.Exists(Path.Combine(gameRoot, QlConstants.DblpackFile))
            || File.Exists(Path.Combine(gameRoot, QlSyncState.StateFile)))
            return gameRoot;

        return null;
    }

    /// <summary>隔离目录是否存在（用于 UI 区分「隔离实例」与「平铺实例」）。</summary>
    public static bool IsIsolatedLayout(string gameRoot)
        => Directory.Exists(Path.Combine(gameRoot, "versions", ManagedInstanceId));
}
