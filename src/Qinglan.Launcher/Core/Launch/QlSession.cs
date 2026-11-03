using System.Security.Cryptography;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>一次启动的身份资料（离线即可；微软登录在 M2b 落地）。</summary>
public sealed record QlProfile(string Name, string Uuid, string AccessToken, string UserType);

/// <summary>会话工厂：离线 UUID 与 Java 端 NameUUIDFromBytes("OfflinePlayer:"+name) 完全同构。</summary>
public static class QlSession
{
    public static QlProfile Offline(string playerName) => new(playerName, OfflineUuid(playerName), "0", "legacy");

    /// <summary>名字 → UUID v3（MD5 大端十六进制，打 v3/变体位）。同名永远同 UUID【正版回退也同】。</summary>
    public static string OfflineUuid(string playerName)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        md5[6] = (byte)((md5[6] & 0x0F) | 0x30); // version = 3
        md5[8] = (byte)((md5[8] & 0x3F) | 0x80); // IETF variant
        return Hex(md5[..4]) + "-" + Hex(md5[4..6]) + "-" + Hex(md5[6..8]) + "-"
               + Hex(md5[8..10]) + "-" + Hex(md5[10..16]); // 大端直排，与 java.util.UUID 一致
    }

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
