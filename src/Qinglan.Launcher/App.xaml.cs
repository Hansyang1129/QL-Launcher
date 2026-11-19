using System.IO;
using System.Windows;
using Qinglan.Launcher.Core;

namespace Qinglan.Launcher;

/// <summary>应用入口：装崩溃日志目录，兜底展示所有未知异常。</summary>
public partial class App : Application
{
    /// <summary>可执行文件所在目录；单文件发布时也指向 exe 而非解包临时目录。</summary>
    private static string BaseDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    /// <summary>启动器数据目录（便携式：紧挨 exe），日志、运行时与 WebView2 缓存都在里面。</summary>
    public static string DataDir { get; } = Path.Combine(BaseDir, "data");

    /// <summary>配置目录：data/config/config.json（与日志/缓存分开，便于备份和迁移）。</summary>
    public static string ConfigDir { get; } = Path.Combine(DataDir, "config");

    public static string LogFile => Path.Combine(DataDir, "launcher.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ConfigDir);
        MigrateLegacyConfig();
        QlDeployment.Load(QlConfig.Load(ConfigDir));
        DispatcherUnhandledException += (_, args) =>
        {
            Globals.Log("未处理异常: " + args.Exception);
            MessageBox.Show("启动器遇到错误，日志已写入:\n" + LogFile + "\n\n" + args.Exception.Message,
                "轻蓝启动器", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }

    /// <summary>把旧版 data/config.json 迁移到 data/config/config.json；失败不阻塞启动。</summary>
    private static void MigrateLegacyConfig()
    {
        try
        {
            string legacy = Path.Combine(DataDir, "config.json");
            string current = Path.Combine(ConfigDir, "config.json");
            if (File.Exists(legacy) && !File.Exists(current))
                File.Move(legacy, current);
        }
        catch (Exception ex)
        {
            Globals.Log("旧配置迁移失败: " + ex.Message);
        }
    }
}
