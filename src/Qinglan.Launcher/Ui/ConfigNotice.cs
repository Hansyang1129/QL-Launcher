using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qinglan.Launcher.Core;

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
}
