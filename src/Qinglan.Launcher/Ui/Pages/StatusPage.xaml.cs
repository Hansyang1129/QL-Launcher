using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Qinglan.Launcher.Core.Status;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>服务器状态页：三个端点的 SLP/HTTP 探测 + 30 秒自动刷新（仅页面可见时）。
/// 探测引擎在 <see cref="QlServerStatus"/>，UI 只负责投影状态与失败原因。</summary>
public partial class StatusPage : UserControl
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);
    private const int ProbeTimeoutMs = 5000;

    private static readonly Brush OnlineBg = Frozen(0xE8, 0xF7, 0xEF);
    private static readonly Brush OfflineBg = Frozen(0xFD, 0xEC, 0xEA);

    private readonly DispatcherTimer _timer = new();
    private bool _busy;
    private bool _visible;

    public StatusPage()
    {
        InitializeComponent();
        _timer.Interval = AutoRefreshInterval;
        _timer.Tick += (_, _) =>
        {
            if (!_busy) _ = RefreshAsync();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _visible = true;
        _timer.Stop();
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _visible = false;
        _timer.Stop();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = RefreshAsync();

    /// <summary>探测整张表并重绘。手动刷新与自动轮询共用一个 busy 闸门。</summary>
    private async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        BtnRefresh.IsEnabled = false;
        RefreshStatusText.Text = "正在探测服务器…";
        SummaryText.Text = "";
        try
        {
            var probes = QlServerStatus.DefaultProbes;
            var results = await QlServerStatus.ProbeAllAsync(probes, ProbeTimeoutMs);
            Render(probes, results);

            int online = results.Count(r => r.Online);
            RefreshStatusText.Text = "于 " + DateTime.Now.ToString("HH:mm:ss") + " 完成探测";
            SummaryText.Text = online + " / " + results.Count + " 个端点在线";
            SummaryText.Foreground = (Brush)FindResource(
                online == results.Count ? "OkGreen" : "TextSecondary");
            if (_visible)
                MainWindow.Instance.SetStatus("服务器状态：" + online + "/" + results.Count + " 在线");
        }
        catch (Exception ex)
        {
            RefreshStatusText.Text = "服务器探测失败";
            SummaryText.Text = ex.Message;
            SummaryText.Foreground = (Brush)FindResource("Danger");
            Globals.Log("服务器状态探测失败: " + ex);
        }
        finally
        {
            _busy = false;
            BtnRefresh.IsEnabled = true;
            if (_visible)
            {
                // 手动与自动探测都重新计时，避免完成瞬间立刻又触发一轮。
                _timer.Stop();
                _timer.Start();
            }
        }
    }

    private void Render(IReadOnlyList<QlProbe> probes, IReadOnlyList<QlProbeResult> results)
    {
        ServerList.Children.Clear();
        for (int i = 0; i < probes.Count && i < results.Count; i++)
            ServerList.Children.Add(BuildRow(probes[i], results[i]));
    }

    private Border BuildRow(QlProbe probe, QlProbeResult result)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 16, 0),
        };
        left.Children.Add(Text(probe.DisplayLabel, 15, FontWeights.Bold));
        left.Children.Add(Text(probe.Address, 11, FontWeights.Normal, "Consolas", "TextSecondary", 2));

        if (result.Online && !string.IsNullOrWhiteSpace(result.Detail))
        {
            var detail = probe.Kind == QlProbeKind.Slp ? VisualMotd(result.Detail!) : result.Detail!;
            if (!string.IsNullOrWhiteSpace(detail))
                left.Children.Add(Wrapped(Text(detail, 13, FontWeights.Normal,
                    brushKey: "TextPrimary", top: 6)));
        }
        if (result.Online && result.Sample.Count > 0)
            left.Children.Add(Wrapped(Text("在服务器：" + string.Join(", ", result.Sample), 12,
                FontWeights.Normal, "Microsoft YaHei UI", "TextSecondary", 4)));
        if (!result.Online && !string.IsNullOrWhiteSpace(result.Detail))
            left.Children.Add(Wrapped(Text("失败原因：" + result.Detail!, 12,
                FontWeights.Normal, "Microsoft YaHei UI", "Danger", 6)));

        var right = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pill = new Border
        {
            Background = result.Online ? OnlineBg : OfflineBg,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 3, 10, 3),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        pill.Child = new TextBlock
        {
            Text = result.Online ? "在线" : "离线",
            Style = (Style)FindResource("BaseText"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource(result.Online ? "OkGreen" : "Danger"),
        };
        right.Children.Add(pill);

        if (result.Online && result.LatencyMs >= 0)
            right.Children.Add(Text("延迟 " + result.LatencyMs + " ms", 12, FontWeights.Normal,
                "Consolas", "TextSecondary", 6, HorizontalAlignment.Right));
        if (result.Online && result.Players is { } players && result.PlayersMax is { } max)
            right.Children.Add(Text("在线 " + players + " / " + max, 12, FontWeights.Normal,
                "Microsoft YaHei UI", "TextSecondary", 2, HorizontalAlignment.Right));

        grid.Children.Add(left);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        card.Child = grid;
        return card;
    }

    private static TextBlock Text(string text, double fontSize, FontWeight weight,
        string family = "Microsoft YaHei UI", string brushKey = "TextPrimary", double top = 0,
        HorizontalAlignment align = HorizontalAlignment.Left)
        => new()
        {
            Text = text,
            Style = (Style)Application.Current.FindResource("BaseText"),
            FontSize = fontSize,
            FontWeight = weight,
            FontFamily = new FontFamily(family),
            Foreground = (Brush)Application.Current.FindResource(brushKey),
            Margin = new Thickness(0, top, 0, 0),
            HorizontalAlignment = align,
        };

    private static TextBlock Wrapped(TextBlock text)
    {
        text.TextWrapping = TextWrapping.Wrap;
        return text;
    }

    /// <summary>MOTD 里可能残留 §x 传统颜色码；只影响展示，探测结果保持原样。</summary>
    private static string VisualMotd(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '§' && i + 1 < text.Length)
            {
                i++; // 跳过颜色/样式控制字符
                continue;
            }
            sb.Append(text[i]);
        }
        return sb.ToString().Trim();
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
