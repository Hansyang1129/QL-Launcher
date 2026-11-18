namespace Qinglan.Launcher.Core.Launch;

/// <summary>
/// 内存上限策略（HMCL 同款口径）：配置 0 = 自动，按物理内存 1/4 取值并夹在 [2048, 8192] MB；
/// 用户选了具体值就照办，不做二次夹取（高级用户可能确实要 16G）。
/// </summary>
public static class QlMemory
{
    public const int MinAutoMb = 2048;
    public const int MaxAutoMb = 8192;

    /// <summary>纯函数：物理内存字节数 → 自动上限（非正值给下限）。</summary>
    public static int AutoMb(long totalBytes)
    {
        if (totalBytes <= 0) return MinAutoMb;
        var quarter = totalBytes / (1024L * 1024L) / 4L;
        return (int)Math.Clamp(quarter, MinAutoMb, MaxAutoMb);
    }

    /// <summary>配置值优先；0 走自动。</summary>
    public static int EffectiveMb(int configuredMb, long totalBytes)
        => configuredMb > 0 ? configuredMb : AutoMb(totalBytes);

    /// <summary>本机可用物理内存（GC 视角；取不到给 0，自动策略会落到下限）。</summary>
    public static long PhysicalBytes()
    {
        try
        {
            return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        }
        catch
        {
            return 0;
        }
    }
}
