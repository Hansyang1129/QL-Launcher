using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Resources;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>首页：大启动按钮 + 轮换壁纸；实例与启动逻辑在 M1/M2 接线。</summary>
public partial class HomePage : UserControl
{
    private static readonly Random Rng = new();

    public HomePage()
    {
        InitializeComponent();
        TryLoadWallpaper();
    }

    /// <summary>随机挑一张内置壁纸（Assets/wallpapers 下 jpg/png；没有就保持渐变底）。</summary>
    private void TryLoadWallpaper()
    {
        try
        {
            string dir = Path.Combine(Globals.AssetsDir, "wallpapers");
            if (!Directory.Exists(dir)) return;
            var shots = Directory.EnumerateFiles(dir)
                .Where(f =>
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    return ext is ".jpg" or ".jpeg" or ".png";
                })
                .ToList();
            if (shots.Count == 0) return;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(shots[Rng.Next(shots.Count)]);
            bmp.EndInit();
            bmp.Freeze();
            Wallpaper.Source = bmp;
            Wallpaper.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Globals.Log("壁纸加载失败: " + ex.Message);
        }
    }

    private void OnLaunch(object sender, RoutedEventArgs e)
    {
        // M2 接线：实例解析 + Java + 启动链；未就绪时给引导提示。
        LaunchHint.Text = "启动链还在搭建（M2）。先去「轻蓝同步」拿到整合包。";
        MainWindow.Instance.Navigate(MainWindow.PageKey.Sync);
    }
}

