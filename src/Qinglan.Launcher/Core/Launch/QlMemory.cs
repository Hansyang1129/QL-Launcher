using System.Runtime.InteropServices;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>本机内存快照：物理总量 + 当前可用量（字节）。</summary>
public sealed record QlMemoryStatus(long TotalBytes, long AvailableBytes)
{
    public static QlMemoryStatus Unknown { get; } = new(0, 0);

    public int TotalMb => ToMb(TotalBytes);
    public int AvailableMb => ToMb(AvailableBytes);

    private static int ToMb(long bytes)
        => bytes <= 0 ? 0 : (int)Math.Min(int.MaxValue, bytes / (1024L * 1024L));
}

/// <summary>
/// 自动内存上限：按**当前可用物理内存**与 mods 数量分阶段估算（PCL 同款思路），
/// 不再用“物理内存 / 4”一刀切。具体值仍由用户在设置页覆盖。
/// </summary>
public static class QlMemory
{
    /// <summary>自动值的下限（MB）。</summary>
    public const int MinAutoMb = 512;

    /// <summary>自动值的上限（MB），仅防异常值。</summary>
    public const int MaxAutoMb = 65536;

    /// <summary>读取物理内存与当前可用内存；失败时回落到 GC 视角的可用量。</summary>
    public static QlMemoryStatus ReadStatus()
    {
        try
        {
            var status = new MemoryStatusEx
            {
                dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>(),
            };
            if (GlobalMemoryStatusEx(ref status))
                return new QlMemoryStatus((long)status.ullTotalPhys,
                    (long)status.ullAvailPhys);
        }
        catch
        {
            // 非 Windows 或 API 不可用：走 GC 回落。
        }

        try
        {
            long available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (available > 0) return new QlMemoryStatus(available, available);
        }
        catch
        {
            // 取不到内存不影响启动。
        }
        return QlMemoryStatus.Unknown;
    }

    /// <summary>物理内存总量（字节）；兼容旧调用。</summary>
    public static long PhysicalBytes() => ReadStatus().TotalBytes;

    /// <summary>
    /// 自动内存（MB）。按可用内存分四阶段，并以 mods 数量抬高目标：
    /// 阶段一 100%、阶段二 70%、阶段三 40%、阶段四 15%；最终至少留约 0.8 GB 给系统。
    /// </summary>
    public static int AutoMb(int modCount, QlMemoryStatus status)
    {
        double availableGb = status.AvailableBytes / (1024.0 * 1024.0 * 1024.0);
        if (availableGb <= 0) return 2048;
        if (modCount < 0) modCount = 0;

        double minimum, target1, target2, target3;
        if (modCount > 0)
        {
            minimum = 0.5 + modCount / 150.0;
            target1 = 1.5 + modCount / 90.0;
            target2 = 2.7 + modCount / 50.0;
            target3 = 4.5 + modCount / 25.0;
        }
        else
        {
            minimum = 0.5;
            target1 = 1.5;
            target2 = 2.5;
            target3 = 4.0;
        }

        double remaining = availableGb;
        double give = Math.Min(remaining, target1);
        remaining -= target1;

        if (remaining >= 0.1)
        {
            double delta = target2 - target1;
            give += Math.Min(remaining * 0.7, delta);
            remaining -= delta / 0.7;
        }
        if (remaining >= 0.1)
        {
            double delta = target3 - target2;
            give += Math.Min(remaining * 0.4, delta);
            remaining -= delta / 0.4;
        }
        if (remaining >= 0.1)
        {
            double delta = target3;
            give += Math.Min(remaining * 0.15, delta);
            remaining -= delta / 0.15;
        }

        give = Math.Max(give, minimum);
        give = Math.Min(give, Math.Max(0.5, availableGb - 0.8));
        int mb = (int)Math.Round(give * 4, MidpointRounding.AwayFromZero) * 256;
        return Math.Clamp(mb, MinAutoMb, MaxAutoMb);
    }

    /// <summary>配置值优先；0 走自动估算。</summary>
    public static int EffectiveMb(int configuredMb, QlMemoryStatus status, int modCount)
        => configuredMb > 0 ? configuredMb : AutoMb(modCount, status);

    /// <summary>统计实例 mods 目录里的可加载文件（jar/zip/litemod，含子目录）。</summary>
    public static int CountMods(string? runDir)
    {
        if (string.IsNullOrWhiteSpace(runDir)) return 0;
        try
        {
            string dir = Path.Combine(runDir, "mods");
            if (!Directory.Exists(dir)) return 0;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Count(path => Path.GetExtension(path).ToLowerInvariant()
                    is ".jar" or ".zip" or ".litemod");
        }
        catch
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);
}
