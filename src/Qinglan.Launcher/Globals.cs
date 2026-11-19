using System.Diagnostics;
using System.IO;
using System.Text;

namespace Qinglan.Launcher;

/// <summary>全局杂项：产品常量、统一日志、外部打开工具。</summary>
public static class Globals
{
    public const string ProductName = "轻蓝启动器";
    public const string PackName = "轻蓝 MTR 4.x";
    public const string Version = "1.0.0";

    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    /// <summary>统一日志：data/launcher.log 逐行追加（时间戳 + 内容），失败静默。</summary>
    public static void Log(string message)
    {
        try
        {
            string dir = App.DataDir;
            Directory.CreateDirectory(dir);
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(' ').Append(message).Append('\n');
            File.AppendAllText(Path.Combine(dir, "launcher.log"), line.ToString(),
                new UTF8Encoding(false));
        }
        catch
        {
            // 日志失败不能拖垮业务，吞掉。
        }
        Debug.WriteLine(message);
    }

    /// <summary>用系统默认程序打开文件夹或链接；失败时写日志并返回 false。</summary>
    public static bool OpenUrl(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Log("OpenUrl 失败 " + target + ": " + ex.Message);
            return false;
        }
    }
}
