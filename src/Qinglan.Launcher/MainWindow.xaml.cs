using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Qinglan.Launcher;

/// <summary>无边框主窗：标题栏 + 侧栏导航 + 页面缓存切换。</summary>
public partial class MainWindow : Window
{
    /// <summary>导航页键，按侧栏顺序排列。</summary>
    public enum PageKey { Home, Sync, Status, Settings }

    private static MainWindow? _current;
    private readonly Dictionary<PageKey, UserControl> _pages = new();
    private PageKey _active;

    public MainWindow()
    {
        InitializeComponent();
        _current = this;
        Navigate(PageKey.Home);
    }

    /// <summary>当前主窗实例，供页面跳转（如引导页跳转同步页）。</summary>
    public static MainWindow Instance => _current ?? throw new InvalidOperationException("主窗尚未创建");

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>侧栏点击： RadioButton 在 NavPanel 里的索引即页键。</summary>
    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb) return;
        int index = NavPanel.Children.IndexOf(rb);
        if (index >= 0 && (PageKey)index != _active)
            Navigate((PageKey)index);
    }

    /// <summary>程序化导航（页面缓存 + 淡入过渡）。</summary>
    public void Navigate(PageKey key)
    {
        if (NavPanel is not null)
        {
            for (int i = 0; i < NavPanel.Children.Count; i++)
            {
                if (NavPanel.Children[i] is RadioButton rb)
                    rb.IsChecked = (PageKey)i == key;
            }
        }
        if (!_pages.TryGetValue(key, out var uc))
        {
            uc = key switch
            {
                PageKey.Sync => new Ui.Pages.SyncPage(),
                PageKey.Status => new Ui.Pages.StatusPage(),
                PageKey.Settings => new Ui.Pages.SettingsPage(),
                _ => new Ui.Pages.HomePage(),
            };
            _pages[key] = uc;
        }
        _active = key;
        SetStatus(PageHint(key));
        Host.Content = uc;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        uc.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>更新侧栏底部状态行（进度提示、探测摘要等）。</summary>
    public void SetStatus(string text) => StatusText.Text = text;

    private static string PageHint(PageKey key) => key switch
    {
        PageKey.Home => "首页",
        PageKey.Sync => "轻蓝同步",
        PageKey.Status => "服务器状态",
        _ => "设置",
    };
}
