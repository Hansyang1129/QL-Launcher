namespace Qinglan.Launcher.Core;

/// <summary>烘进启动器的轻蓝固定设定；单一事实来源。</summary>
public static class QlConstants
{
    /// <summary>资源服务器 manifest 地址（正式服务器定稿前是预备值）。</summary>
    public const string DefaultManifestUrl = "https://files.example.com/qinglan/manifest.json";

    /// <summary>被禁用可选组文件的落盘后缀；sync/种子导入/dblpack 共用。</summary>
    public const string DisabledSuffix = ".disabled";

    /// <summary>实例内容清单文件名（种子导入生成、每次成功 sync 刷新）。</summary>
    public const string DblpackFile = "dblpack.json";

    /// <summary>RPU 更新服务 apiBase（部署配置缺省值；可被启动器旁 qlrpu-client.json 覆盖）。</summary>
    public const string DefaultRpuApiBase = "https://rpu.example.com/api/v1/client";

    /// <summary>RPU 发布清单 Ed25519 部署公钥（SPKI DER 的 base64，末 32 字节为裸钥）。</summary>
    public const string DefaultRpuSigningPublicKey = "REMOVED-RPU-KEY";
}
