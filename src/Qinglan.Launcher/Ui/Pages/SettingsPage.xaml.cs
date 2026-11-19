using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Launch;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>设置页：游戏目录、Java、JVM/内存、微软 client id（可选）+ 日志/关于。</summary>
public partial class SettingsPage : UserControl
{
    private bool _loadingSettings;

    public SettingsPage() => InitializeComponent();

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => LoadSettings();
    }

    private void LoadSettings()
    {
        var cfg = QlConfig.Load(App.ConfigDir);
        GameDirText.Text = string.IsNullOrWhiteSpace(cfg.GameDir) ? "（未设置）" : cfg.GameDir;
        JavaDirText.Text = string.IsNullOrWhiteSpace(cfg.JavaDir) ? "自动探测" : cfg.JavaDir;
        ClientIdBox.Text = cfg.MicrosoftClientId ?? "";
        ClientIdStatusText.Text = "";
        UpdateJavaStatus(cfg.JavaDir);

        _loadingSettings = true;
        try
        {
            MemoryInputBox.Text = cfg.MaxMemoryMb > 0 ? cfg.MaxMemoryMb.ToString() : "";
            JvmPrettyCheck.IsChecked = cfg.JvmArgsOnePerLine;
            UseVersionJvmCheck.IsChecked = cfg.UseVersionJvmArgs;
            JvmArgsBox.AcceptsReturn = cfg.JvmArgsOnePerLine;
            JvmArgsBox.Text = cfg.JvmArgsOnePerLine
                ? QlJvmOptions.FormatPretty(cfg.JvmArgs)
                : (cfg.JvmArgs ?? "");
        }
        finally
        {
            _loadingSettings = false;
        }
        ValidateJvm(false);
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

    private void OnMemoryPreset(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tag) return;
        MemoryInputBox.Text = tag == "0" ? "" : tag;
        ValidateJvm(false);
    }

    private void OnJvmInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_loadingSettings || JvmPreviewBox is null) return;
        ValidateJvm(false);
    }

    private void OnJvmPrettyChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || JvmArgsBox is null) return;
        bool pretty = JvmPrettyCheck.IsChecked == true;
        string raw = pretty ? QlJvmOptions.FormatRaw(JvmArgsBox.Text) : JvmArgsBox.Text;
        _loadingSettings = true;
        try
        {
            JvmArgsBox.AcceptsReturn = pretty;
            JvmArgsBox.Text = pretty ? QlJvmOptions.FormatPretty(raw) : QlJvmOptions.FormatRaw(raw);
        }
        finally
        {
            _loadingSettings = false;
        }
        ValidateJvm(false);
    }

    private void OnJvmVersionChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings || JvmPreviewBox is null) return;
        ValidateJvm(false);
    }

    private void OnFillMojangJvm(object sender, RoutedEventArgs e)
    {
        var args = LoadMojangJvmArgs();
        if (args.Count == 0)
        {
            JvmStatusText.Text = "当前实例没有可读取的版本 JSON JVM 参数；先到「轻蓝同步」完成一次包应用。";
            JvmStatusText.Foreground = (Brush)FindResource("Danger");
            return;
        }

        string raw = QlJvmOptions.JoinTokens(args);
        _loadingSettings = true;
        try
        {
            JvmArgsBox.AcceptsReturn = JvmPrettyCheck.IsChecked == true;
            JvmArgsBox.Text = JvmPrettyCheck.IsChecked == true
                ? QlJvmOptions.FormatPretty(raw)
                : raw;
            UseVersionJvmCheck.IsChecked = false; // 已复制到下方参数框，避免重复注入
        }
        finally
        {
            _loadingSettings = false;
        }
        if (ValidateJvm(false))
            JvmStatusText.Text = "已填入版本 JSON 参数并关闭重复注入。" + JvmStatusText.Text;
    }

    private static List<string> LoadMojangJvmArgs()
    {
        try
        {
            var cfg = QlConfig.Load(App.ConfigDir);
            if (string.IsNullOrWhiteSpace(cfg.GameDir) || !Directory.Exists(cfg.GameDir))
                return new List<string>();
            var runDir = QlInstances.ResolveRunDir(cfg.GameDir);
            if (runDir is null) return new List<string>();
            var json = Path.Combine(runDir, QlInstances.ManagedInstanceId + ".json");
            if (!File.Exists(json)) return new List<string>();
            var version = QlVersionJson.Parse(File.ReadAllText(json));
            return QlArgsBuilder.ExpandJvmTemplate(version.JvmArgs);
        }
        catch (Exception ex)
        {
            Globals.Log("读取版本 JSON JVM 参数失败: " + ex.Message);
            return new List<string>();
        }
    }

    private void OnSaveJvm(object sender, RoutedEventArgs e) => ValidateJvm(true);

    /// <summary>校验并更新预览；save=true 时只保存合法值。错误只走行内提示。</summary>
    private bool ValidateJvm(bool save)
    {
        if (_loadingSettings || JvmPreviewBox is null || JvmStatusText is null) return false;

        if (!QlJvmOptions.TryParseMemory(MemoryInputBox.Text, out int enteredMb,
                out string? memoryError))
        {
            JvmStatusText.Text = memoryError;
            JvmStatusText.Foreground = (Brush)FindResource("Danger");
            JvmPreviewBox.Text = "内存输入无效。";
            return false;
        }

        string raw = JvmPrettyCheck.IsChecked == true
            ? QlJvmOptions.FormatRaw(JvmArgsBox.Text)
            : (JvmArgsBox.Text ?? "");
        if (JvmPrettyCheck.IsChecked != true &&
            (raw.Contains('\n') || raw.Contains('\r')))
        {
            JvmStatusText.Text = "未勾选「每个参数一行」时不能换行。";
            JvmStatusText.Foreground = (Brush)FindResource("Danger");
            JvmPreviewBox.Text = "额外 JVM 参数含换行。";
            return false;
        }

        long physicalBytes = QlMemory.PhysicalBytes();
        int effectiveMb = QlMemory.EffectiveMb(enteredMb, physicalBytes);
        if (!QlJvmOptions.TryParse(raw, effectiveMb, out QlJvmPlan plan, out string? argError))
        {
            JvmStatusText.Text = argError;
            JvmStatusText.Foreground = (Brush)FindResource("Danger");
            JvmPreviewBox.Text = "额外 JVM 参数无效。";
            return false;
        }

        var warnings = new List<string>();
        long physicalMb = physicalBytes / (1024L * 1024L);
        if (physicalMb > 0 && effectiveMb > physicalMb)
            warnings.Add("内存上限超过本机物理内存（" + physicalMb + " MB）");
        if (physicalMb > 0 && plan.XmxMb > physicalMb)
            warnings.Add("-Xmx 超过本机物理内存（" + physicalMb + " MB）");
        if (enteredMb > 0 && plan.UserProvidedXmx)
            warnings.Add("命令行使用你填写的 " + plan.XmxToken + "，内存输入仅作默认值");
        if (plan.XmxMb < 1024)
            warnings.Add("内存低于 1024 MB，部分整合包可能无法启动");

        JvmPreviewBox.Text = string.Join(Environment.NewLine,
            QlJvmOptions.BuildPreview(plan, UseVersionJvmCheck.IsChecked == true));

        string status = warnings.Count > 0
            ? "提醒：" + string.Join("；", warnings) + "。"
            : "参数有效。";
        JvmStatusText.Text = status;
        JvmStatusText.Foreground = (Brush)FindResource(
            warnings.Count > 0 ? "TextSecondary" : "OkGreen");

        if (!save) return true;

        try
        {
            var cfg = QlConfig.Load(App.ConfigDir);
            cfg.MaxMemoryMb = enteredMb;
            cfg.JvmArgs = string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
            cfg.JvmArgsOnePerLine = JvmPrettyCheck.IsChecked == true;
            cfg.UseVersionJvmArgs = UseVersionJvmCheck.IsChecked == true;
            cfg.Save(App.ConfigDir);
            JvmStatusText.Text = warnings.Count > 0
                ? "已保存（有提醒）：" + string.Join("；", warnings) + "。"
                : "已保存。启动时会使用上面的 JVM 参数。";
            JvmStatusText.Foreground = (Brush)FindResource(
                warnings.Count > 0 ? "TextSecondary" : "OkGreen");
            return true;
        }
        catch (Exception ex)
        {
            JvmStatusText.Text = "保存失败：" + ex.Message;
            JvmStatusText.Foreground = (Brush)FindResource("Danger");
            Globals.Log("JVM 设置保存失败: " + ex.Message);
            return false;
        }
    }

    private void OnPickDir(object sender, RoutedEventArgs e)
    {
        var dir = Pickers.Folder(Window.GetWindow(this));
        if (dir is null) return;
        try
        {
            var cfg = QlConfig.Load(App.ConfigDir);
            cfg.GameDir = dir;
            cfg.Save(App.ConfigDir);
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
            var cfg = QlConfig.Load(App.ConfigDir);
            cfg.JavaDir = dir;
            cfg.Save(App.ConfigDir);
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
            var cfg = QlConfig.Load(App.ConfigDir);
            cfg.JavaDir = null;
            cfg.Save(App.ConfigDir);
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
            var cfg = QlConfig.Load(App.ConfigDir);
            cfg.MicrosoftClientId = string.IsNullOrWhiteSpace(ClientIdBox.Text)
                ? null
                : ClientIdBox.Text.Trim();
            cfg.Save(App.ConfigDir);
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
        var cfg = QlConfig.Load(App.ConfigDir);
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
