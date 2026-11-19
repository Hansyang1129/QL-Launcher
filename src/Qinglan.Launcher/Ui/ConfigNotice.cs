using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Diagnostics;

namespace Qinglan.Launcher.Ui;

/// <summary>
/// “配置未就绪”的统一投影：文案与卡片样式只在这里定义，页面不自造占位。
/// <para>字段路径与 <c>config.example.json</c> 保持一致；开源版空配置时所有页面共用。</para>
/// </summary>
internal static class ConfigNotice
{
    /// <summary>状态页：三个探测来源都没配。</summary>
    public static string Status
        => QlDeployment.MissingHint("服务器状态",
            "deployment.server / deployment.mtr / deployment.reporter");

    /// <summary>MTR 页：地图 API 未配。</summary>
    public static string Mtr
        => QlDeployment.MissingHint("MTR 轨交服务", "deployment.mtr.baseUrl");

    /// <summary>地图页：网页地图入口未配。</summary>
    public static string Map
        => QlDeployment.MissingHint("MTR 地图", "deployment.mtr.baseUrl");

    /// <summary>论坛页：站点根地址未配。</summary>
    public static string Forum
        => QlDeployment.MissingHint("论坛", "deployment.forum.baseUrl");

    /// <summary>生成统一占位卡片（可直接加入任意 StackPanel）。</summary>
    public static Border Build(string message)
    {
        var text = new TextBlock
        {
            Text = message,
            Style = (Style)Application.Current.FindResource("BaseText"),
            FontSize = 13,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
        };
        return new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
            Child = text,
        };
    }

    /// <summary>本地错误归因卡：结论 + 建议 + 可展开原始日志；不新开窗口、不上报。</summary>
    public static Border Build(QlDiagnosis diagnosis)
    {
        var panel = new StackPanel();

        panel.Children.Add(new TextBlock
        {
            Text = diagnosis.Title,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = diagnosis.Conclusion,
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 12.5,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });

        foreach (string suggestion in diagnosis.Suggestions)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "• " + suggestion,
                Margin = new Thickness(0, 4, 0, 0),
                FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = (Brush)Application.Current.FindResource("TextPrimary"),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19,
            });
        }

        string fingerprint = diagnosis.Fingerprint.Length > 12
            ? diagnosis.Fingerprint[..12] + "…"
            : diagnosis.Fingerprint;
        panel.Children.Add(new TextBlock
        {
            Text = "日志指纹 " + fingerprint + " · " + diagnosis.SizeBytes + " B",
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 10.5,
            FontFamily = new FontFamily("Consolas"),
            Foreground = (Brush)Application.Current.FindResource("TextSecondary"),
        });

        var raw = new TextBox
        {
            Text = diagnosis.RawExcerpt,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 240,
            MinHeight = 80,
            Padding = new Thickness(8),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10.5,
            Background = new SolidColorBrush(Color.FromRgb(0xF7, 0xFA, 0xFF)),
            BorderBrush = (Brush)Application.Current.FindResource("CardBorder"),
            BorderThickness = new Thickness(1),
        };
        panel.Children.Add(new Expander
        {
            Header = "查看原始日志",
            Content = raw,
            IsExpanded = false,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
        });

        return new Border
        {
            Style = (Style)Application.Current.FindResource("Card"),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 10),
            Child = panel,
        };
    }
}
