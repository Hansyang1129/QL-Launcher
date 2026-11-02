using System.IO;
using System.Windows;

namespace Qinglan.Launcher;

/// <summary>应用入口：装崩溃日志目录，兜底展示所有未知异常。</summary>
public partial class App : Application
{
    /// <summary>启动器数据目录（便携式：紧挨 exe），配置与日志都在里面。</summary>
    public static string DataDir { get; } =
        Path.Combine(AppContext.BaseDirectory, "data");

    public static string LogFile => Path.Combine(DataDir, "launcher.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        DispatcherUnhandledException += (_, args) =>
        {
            Globals.Log("未处理异常: " + args.Exception);
            MessageBox.Show("启动器遇到错误，日志已写入:\n" + LogFile + "\n\n" + args.Exception.Message,
                "轻蓝启动器", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
