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

    /// <summary>发布器给清单条目兜底的资源服务器 URL 前缀（QLManifestTool.DEFAULT_BASE_URL 同值）。</summary>
    /// <para>只作为 CDN 地址之后的最后候选出现在 manifest 条目里：它从未真正上线过，
    /// 下载失败只会记一条 warning 然后换下一个候选，因此保留它与 QL-HMCL 行为逐字节对齐。</para>
    public const string PublisherFallbackBaseUrl = "https://files.example.com/qinglan/files/";

    /// <summary>RPU 更新服务 apiBase（部署配置缺省值；可被启动器旁 qlrpu-client.json 覆盖）。</summary>
    public const string DefaultRpuApiBase = "https://rpu.example.com/api/v1/client";

    /// <summary>RPU 发布清单 Ed25519 部署公钥（SPKI DER 的 base64，末 32 字节为裸钥）。</summary>
    public const string DefaultRpuSigningPublicKey = "REMOVED-RPU-KEY";
}
