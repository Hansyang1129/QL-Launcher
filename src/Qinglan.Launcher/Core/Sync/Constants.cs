namespace Qinglan.Launcher.Core;

/// <summary>烘进启动器的轻蓝固定设定；单一事实来源。</summary>
public static class QlConstants
{
    /// <summary>被禁用可选组文件的落盘后缀；sync/种子导入/dblpack 共用。</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>实例内容清单文件名（每次成功 sync 刷新）。</summary>
    public const string DblpackFile = "dblpack.json";

    /// <summary>实例内的整合包缓存目录；同步页在用户没现场挑包时回落到这里取最新的 .mrpack。</summary>
    /// <para>QL-HMCL 同名约定（QLPackSync.CACHE_DIR）。启动器刻意不把任何远端地址烘进构建。</para>
    public const string PackCacheDir = ".ql-pack";
}
