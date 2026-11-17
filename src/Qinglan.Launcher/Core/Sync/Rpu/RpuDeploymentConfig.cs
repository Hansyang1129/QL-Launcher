using System.IO;
using System.Text.Json;

namespace Qinglan.Launcher.Core.Sync.Rpu;

/// <summary>
/// RPU 部署配置：更新服务地址与发布清单签名公钥。
/// 优先读取启动器旁的 <c>qlrpu-client.json</c>（与 Mod 配置同形：apiBase / signingPublicKey），
/// 缺失或损坏时回落内置常量——公钥是公开信息，烤进程序没有安全负担。
/// </summary>
public static class RpuDeploymentConfig
{
    /// <summary>读取部署配置：返回规范化前的 apiBase 原文与 32 字节裸签名公钥。</summary>
    public static (string ApiBase, byte[] SigningKey) Load()
    {
        var apiBase = QlConstants.DefaultRpuApiBase;
        var keyBase64 = QlConstants.DefaultRpuSigningPublicKey;
        try
        {
            var file = Path.Combine(AppContext.BaseDirectory, "qlrpu-client.json");
            if (File.Exists(file))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("apiBase", out var ab)
                    && ab.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(ab.GetString()))
                    apiBase = ab.GetString()!;
                if (doc.RootElement.TryGetProperty("signingPublicKey", out var sp)
                    && sp.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sp.GetString()))
                    keyBase64 = sp.GetString()!;
            }
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Globals.Log("读取 qlrpu-client.json 失败，使用内置部署配置：" + e.Message);
        }
        return (apiBase, DecodeSigningKey(keyBase64));
    }

    /// <summary>SPKI DER 的 base64 → 末尾 32 字节裸 Ed25519 公钥（部署密钥的标准形态）。</summary>
    public static byte[] DecodeSigningKey(string spkiBase64)
    {
        byte[] der;
        try { der = Convert.FromBase64String(spkiBase64.Trim()); }
        catch (FormatException) { throw new RpuException("部署配置中的签名公钥无效。"); }
        if (der.Length < 32)
            throw new RpuException("部署配置中的签名公钥无效。");
        return der.AsSpan(der.Length - 32).ToArray();
    }
}
