using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>
/// 启动前内存整理：对可访问进程调用 <c>EmptyWorkingSet</c>，降低当前物理内存占用。
/// 与 PCL 的“内存优化”同思路；无权限/系统进程失败直接跳过，不影响启动。
/// </summary>
public static class QlMemoryOptimizer
{
    /// <summary>整理所有可访问进程的工作集；<paramref name="timeoutMs"/> 防止卡太久。</summary>
    public static int TrimAll(int timeoutMs = 3000)
    {
        int trimmed = 0;
        var watch = Stopwatch.StartNew();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return 0;
        }

        foreach (var process in processes)
        {
            if (watch.ElapsedMilliseconds >= timeoutMs) break;
            try
            {
                if (EmptyWorkingSet(process.Handle)) trimmed++;
            }
            catch
            {
                // 系统进程/权限不足：跳过。
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            using var self = Process.GetCurrentProcess();
            if (EmptyWorkingSet(self.Handle)) trimmed++;
        }
        catch
        {
            // 自身整理失败也无所谓。
        }
        return trimmed;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);
}
