using System.IO;
using System.Windows;
using System.Windows.Controls;
using Qinglan.Launcher.Core;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>设置页：游戏目录、Java、微软 client id（可选）+ 日志/关于。</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        var cfg = QlConfig.Load(App.DataDir);
        GameDirText.Text = string.IsNullOrWhiteSpace(cfg.GameDir) ? "（未设置）" : cfg.GameDir;
        JavaDirText.Text = string.IsNullOrWhiteSpace(cfg.JavaDir) ? "自动探测" : cfg.JavaDir;
        ClientIdBox.Text = cfg.MicrosoftClientId ?? "";
        ClientIdStatusText.Text = "";
        UpdateJavaStatus(cfg.JavaDir);
    }

    private void UpdateJavaStatus(string? javaDir)
    {
        if (string.IsNullOrWhiteSpace(javaDir))
        {
            JavaStatusText.Text = "当前为自动探测；启动时若找不到会下载 Mojang 官方运行时。";
            return;
        }
        var exe = Path.Combine(javaDir, "bin", "java.exe");
        JavaStatusText.Text = File.Exists(exe)
            ? "已指定：" + exe
            : "该目录下没有 bin\\java.exe，启动时会回退到自动探测。";
        JavaStatusText.Foreground = File.Exists(exe)
            ? (System.Windows.Media.Brush)FindResource("TextSecondary")
            : (System.Windows.Media.Brush)FindResource("Danger");
    }

    private void OnPickDir(object sender, RoutedEventArgs e)
    {
        var dir = Pickers.Folder(Window.GetWindow(this));
        if (dir is null) return;
        try
        {
            var cfg = QlConfig.Load(App.DataDir);
            cfg.GameDir = dir;
            cfg.Save(App.DataDir);
        }
        catch (Exception ex)
        {
            Globals.Log("游戏目录保存失败: " + ex.Message);
        }
        LoadSettings();
    }

    private void OnPickJava(object sender, RoutedEventArgs e)
    {
        var dir = Pickers.Folder(Window.GetWindow(this));
        if (dir is null) return;
        try
        {
            var cfg = QlConfig.Load(App.DataDir);
            cfg.JavaDir = dir;
            cfg.Save(App.DataDir);
        }
        catch (Exception ex)
        {
            Globals.Log("Java 目录保存失败: " + ex.Message);
        }
        LoadSettings();
    }

    private void OnAutoJava(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = QlConfig.Load(App.DataDir);
            cfg.JavaDir = null;
            cfg.Save(App.DataDir);
        }
        catch (Exception ex)
        {
            Globals.Log("Java 目录保存失败: " + ex.Message);
        }
        LoadSettings();
    }

    private void OnSaveClientId(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = QlConfig.Load(App.DataDir);
            cfg.MicrosoftClientId = string.IsNullOrWhiteSpace(ClientIdBox.Text)
                ? null
                : ClientIdBox.Text.Trim();
            cfg.Save(App.DataDir);
            ClientIdStatusText.Text = string.IsNullOrWhiteSpace(cfg.MicrosoftClientId)
                ? "已清空：微软登录入口保持隐藏，离线账号照常可用。"
                : "已保存：回首页即可看到「登录微软账号」。";
        }
        catch (Exception ex)
        {
            ClientIdStatusText.Text = "保存失败：" + ex.Message;
            Globals.Log("client id 保存失败: " + ex.Message);
        }
    }

    private void OnOpenDir(object sender, RoutedEventArgs e)
    {
        var cfg = QlConfig.Load(App.DataDir);
        if (string.IsNullOrWhiteSpace(cfg.GameDir) || !Directory.Exists(cfg.GameDir))
            return;
        Globals.OpenUrl(cfg.GameDir);
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.DataDir);
        Globals.OpenUrl(App.DataDir);
    }
}
