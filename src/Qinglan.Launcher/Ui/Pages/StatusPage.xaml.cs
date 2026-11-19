using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Status;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>服务器状态页：三个端点的 SLP/HTTP 探测 + 30 秒自动刷新（仅页面可见时）。
/// 探测引擎在 <see cref="QlServerStatus"/>，UI 只负责投影状态与失败原因。</summary>
public partial class StatusPage : UserControl
{
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);
    private const int ProbeTimeoutMs = 5000;

    /// <summary>玩家胶囊最多渲染的个数，防止超大 sample 拖垮布局。</summary>
    private const int MaxPlayerChips = 64;

    private static readonly Brush OnlineBg = Frozen(0xE8, 0xF7, 0xEF);
    private static readonly Brush OfflineBg = Frozen(0xFD, 0xEC, 0xEA);
    private static readonly Brush ChipBg = Frozen(0xEA, 0xF3, 0xFD);

    private readonly DispatcherTimer _timer = new();
    private CancellationTokenSource? _cts;
    private IReadOnlyList<QlProbe> _probes = Array.Empty<QlProbe>();
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
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        _probes = QlServerStatus.BuildDefaultProbes();
        if (_probes.Count == 0)
        {
            ShowUnconfigured();
            return;
        }

        BtnRefresh.IsEnabled = true;
        RefreshHintText.Text = "停留本页时每 30 秒自动刷新；离开页面即暂停。";
        _timer.Stop();
        _timer.Start();
        _ = RefreshAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _visible = false;
        _timer.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        _probes = QlServerStatus.BuildDefaultProbes();
        if (_probes.Count == 0)
        {
            ShowUnconfigured();
            return;
        }
        _ = RefreshAsync();
    }

    /// <summary>探测整张表并重绘。手动刷新与自动轮询共用一个 busy 闸门。</summary>
    private async Task RefreshAsync()
    {
        if (_busy) return;
        var probes = _probes;
        if (probes.Count == 0)
        {
            ShowUnconfigured();
            return;
        }

        var ct = _cts?.Token ?? CancellationToken.None;
        _busy = true;
        BtnRefresh.IsEnabled = false;
        RefreshStatusText.Text = "正在探测服务器…";
        SummaryText.Text = "";
        try
        {
            var results = await QlServerStatus.ProbeAllAsync(probes, ProbeTimeoutMs, ct);
            if (ct.IsCancellationRequested) return;
            Render(probes, results);

            int online = results.Count(r => r.Online);
            RefreshStatusText.Text = "于 " + DateTime.Now.ToString("HH:mm:ss") + " 完成探测";
            SummaryText.Text = online + " / " + results.Count + " 个端点在线";
            SummaryText.Foreground = (Brush)FindResource(
                online == results.Count ? "OkGreen" : "TextSecondary");
            if (_visible)
                MainWindow.Instance.SetStatus("服务器状态：" + online + "/" + results.Count + " 在线");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 离页取消：保留上一轮结果，不写入失败提示。
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
            BtnRefresh.IsEnabled = !ct.IsCancellationRequested;
            if (_visible && !ct.IsCancellationRequested && probes.Count > 0)
            {
                // 手动与自动探测都重新计时，避免完成瞬间立刻又触发一轮。
                _timer.Stop();
                _timer.Start();
            }
        }
    }

    /// <summary>三个来源都为空：统一占位，不启动轮询、不发请求。</summary>
    private void ShowUnconfigured()
    {
        _timer.Stop();
        BtnRefresh.IsEnabled = false;
        _probes = Array.Empty<QlProbe>();
        ServerList.Children.Clear();
        ServerList.Children.Add(ConfigNotice.Build(ConfigNotice.Status));
        RefreshStatusText.Text = "未配置";
        SummaryText.Text = "状态探测所需端点为空";
        SummaryText.Foreground = (Brush)FindResource("TextSecondary");
        RefreshHintText.Text = "配置后自动启用 30 秒轮询；当前不发请求。";
        PlayerChips.Children.Clear();
        PlayerCountText.Text = "—";
        ShowPlayerHint("未配置 SLP 端点。");
        PlayerSourceText.Text = "";
        if (_visible)
            MainWindow.Instance.SetStatus("服务器状态：未配置");
        Globals.Log("状态页未配置：deployment.server / mtr / reporter 均为空");
    }

    private void Render(IReadOnlyList<QlProbe> probes, IReadOnlyList<QlProbeResult> results)
    {
        ServerList.Children.Clear();
        for (int i = 0; i < probes.Count && i < results.Count; i++)
            ServerList.Children.Add(BuildRow(probes[i], results[i]));
        RenderPlayers(probes, results);
    }

    /// <summary>在线玩家卡片：取 SLP 结果的人数与玩家样本，样本缺失时给出原因说明。</summary>
    private void RenderPlayers(IReadOnlyList<QlProbe> probes, IReadOnlyList<QlProbeResult> results)
    {
        PlayerChips.Children.Clear();

        int index = -1;
        for (int i = 0; i < probes.Count && i < results.Count; i++)
        {
            if (probes[i].Kind != QlProbeKind.Slp) continue;
            index = i;
            break;
        }
        if (index < 0)
        {
            PlayerCountText.Text = "—";
            ShowPlayerHint("未配置 SLP 端点。");
            PlayerSourceText.Text = "";
            return;
        }

        var result = results[index];
        if (!result.Online)
        {
            PlayerCountText.Text = "—";
            ShowPlayerHint("服务器离线，无法获取在线玩家。");
            PlayerSourceText.Text = "数据来自 Minecraft SLP 状态查询。";
            return;
        }

        int online = result.Players ?? 0;
        int max = result.PlayersMax ?? 0;
        PlayerCountText.Text = "在线 " + online + " / " + max;
        PlayerSourceText.Text = "数据来自 Minecraft SLP 状态查询。";

        if (result.Sample.Count == 0)
        {
            ShowPlayerHint(online == 0
                ? "当前无玩家在线。"
                : "服务器未公开玩家名单（在线 " + online + " 人）。");
            return;
        }

        PlayerEmptyText.Visibility = Visibility.Collapsed;
        int shown = Math.Min(result.Sample.Count, MaxPlayerChips);
        for (int i = 0; i < shown; i++)
            PlayerChips.Children.Add(PlayerChip(result.Sample[i]));

        if (result.Sample.Count > shown)
            PlayerSourceText.Text = "仅显示前 " + shown + " 位玩家（共 " + result.Sample.Count + " 位）。";
        else if (shown < online)
            PlayerSourceText.Text = "仅显示服务器公开的 " + shown + " 位玩家。";
    }

    private void ShowPlayerHint(string text)
    {
        PlayerEmptyText.Text = text;
        PlayerEmptyText.Visibility = Visibility.Visible;
    }

    private Border PlayerChip(string name)
    {
        var chip = new Border
        {
            Background = ChipBg,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 5, 12, 5),
            Margin = new Thickness(0, 0, 8, 8),
        };
        chip.Child = new TextBlock
        {
            Text = name,
            Style = (Style)FindResource("BaseText"),
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextPrimary"),
        };
        return chip;
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
