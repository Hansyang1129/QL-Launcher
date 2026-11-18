using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Qinglan.Launcher.Core.Mtr;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>
/// MTR 地图页：用 WebView2 内嵌 <see cref="QlMtr.WebMapUrl"/>。
/// <para>用户明确要求“能嵌就嵌，不行就提示 webview 不可用”：运行时缺失、初始化失败或
/// 主导航失败都切到同一块提示面板，并保留系统浏览器入口。</para>
/// </summary>
public partial class MapPage : UserControl
{
    private bool _ready;
    private bool _initializing;
    private bool _navigatingMap;

    public MapPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = EnsureWebViewAsync();
        Unloaded += (_, _) => LoadingPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>首次进入页面时初始化 WebView2；页面被缓存，后续返回不再重建。</summary>
    private async Task EnsureWebViewAsync()
    {
        if (_ready || _initializing) return;
        _initializing = true;
        ShowLoading("首次打开会初始化 WebView2 用户目录。");
        try
        {
            string? version;
            try
            {
                version = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (WebView2RuntimeNotFoundException)
            {
                version = null;
            }
            if (string.IsNullOrEmpty(version))
            {
                ShowFallback("webview 不可用",
                    "本机没有检测到 WebView2 运行时。安装 Microsoft Edge WebView2 Runtime 后重试，"
                    + "或直接在系统浏览器中查看地图。");
                return;
            }

            string userDataDir = Path.Combine(App.DataDir, "webview2");
            Directory.CreateDirectory(userDataDir);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataDir);
            await Web.EnsureCoreWebView2Async(environment);

            var core = Web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            // 地图里的站名/线路弹窗常开新窗口：交给系统浏览器，避免页内再叠一个无头窗口。
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Globals.OpenUrl(e.Uri);
            };

            _ready = true;
            Globals.Log("MTR 地图：WebView2 " + version + " 开始加载 " + QlMtr.WebMapUrl);
            StartMapNavigation();
        }
        catch (Exception ex)
        {
            ShowFallback("webview 不可用", "WebView2 初始化失败：" + ex.Message);
            Globals.Log("MTR 地图 WebView2 初始化失败: " + ex);
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>发起地图导航。只认这次导航的完成事件，避免初始化阶段的 about:blank 中止误判。</summary>
    private void StartMapNavigation()
    {
        if (Web.CoreWebView2 is null) return;
        _navigatingMap = true;
        ShowLoading("正在连接 " + QlMtr.WebMapUrl + " …");
        Web.CoreWebView2.Navigate(QlMtr.WebMapUrl);
        _ = NavigationTimeoutAsync();
    }

    /// <summary>导航迟迟不完成时的兜底提示；若真实导航稍后成功，完成事件仍会切回地图。</summary>
    private async Task NavigationTimeoutAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(15));
        if (!_navigatingMap) return;
        ShowFallback("webview 地图加载超时",
            "连接 " + QlMtr.WebMapUrl + " 超过 15 秒未完成。可重试或在浏览器打开。");
        Globals.Log("MTR 地图导航超时");
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        Globals.Log("MTR 地图开始导航: " + e.Uri);
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        string source = Web.CoreWebView2?.Source ?? "";
        Globals.Log("MTR 地图导航完成: " + (e.IsSuccess ? "成功" : e.WebErrorStatus.ToString())
            + " source=" + source);
        if (!_navigatingMap) return;

        // WebView2 初始化时自带一次 about:blank 导航，控件被隐藏/重建时可能以 Unknown 中止；
        // 它不是地图导航，忽略掉，否则会误切到失败面板并中止真正的地图加载。
        if (source.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return;

        _navigatingMap = false;
        if (e.IsSuccess)
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            FallbackPanel.Visibility = Visibility.Collapsed;
            Web.Visibility = Visibility.Visible;
            Globals.Log("MTR 地图加载完成");
            return;
        }

        ShowFallback("webview 地图加载失败",
            "导航未完成（" + e.WebErrorStatus + "）。服务器地图服务可能暂时不可用，可重试或在浏览器打开。");
        Globals.Log("MTR 地图导航失败: " + e.WebErrorStatus);
    }

    /// <summary>重新加载：初始化过就直接导航，否则重走一遍运行时检测。</summary>
    private void OnReload(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 is not null)
        {
            StartMapNavigation();
            return;
        }
        _ = EnsureWebViewAsync();
    }

    private void OnOpenExternal(object sender, RoutedEventArgs e)
    {
        if (!Globals.OpenUrl(QlMtr.WebMapUrl))
            FallbackDetail.Text = "无法调起系统浏览器，请手动访问 " + QlMtr.WebMapUrl;
    }

    private void ShowLoading(string detail)
    {
        Web.Visibility = Visibility.Collapsed;
        FallbackPanel.Visibility = Visibility.Collapsed;
        LoadingDetail.Text = detail;
        LoadingPanel.Visibility = Visibility.Visible;
    }

    /// <summary>切到提示面板。WebView2 必须先隐藏，否则 HWND 会盖住 WPF 覆盖层。</summary>
    private void ShowFallback(string title, string detail)
    {
        Web.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Collapsed;
        FallbackTitle.Text = title;
        FallbackDetail.Text = detail;
        FallbackPanel.Visibility = Visibility.Visible;
    }
}
