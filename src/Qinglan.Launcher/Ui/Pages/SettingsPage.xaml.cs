using System.IO;
using System.Windows;
using System.Windows.Controls;
using Qinglan.Launcher.Core;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>设置页：游戏目录（v1 的关键配置）+ 日志/关于。</summary>
public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => LoadGameDir();
    }

    private void LoadGameDir()
    {
        var cfg = QlConfig.Load(App.DataDir);
        GameDirText.Text = string.IsNullOrWhiteSpace(cfg.GameDir) ? "（未设置）" : cfg.GameDir;
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
        LoadGameDir();
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
