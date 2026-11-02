using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>轻蓝同步页。语义对齐 HMCL-QL 的 QLSyncPage：查更新（fetch+analyze）→ 应用（sync），
/// 页面再现即刷新实例状态并锁应用（M13/M14）；种子导入 = mrpack 安装 + seedize（阶段2 链）。</summary>
public partial class SyncPage : UserControl
{
    private QlConfig _config = new();
    private QlSyncState? _state;
    private QlManifest? _fetched;
    private string? _manifestSourceUrl;
    private CancellationTokenSource? _cts;
    private bool _busy;

    public SyncPage()
    {
        InitializeComponent();
        // M14 同效：URL 一动，已抓的 manifest 就作废，应用键锁住。
        ManifestUrlBox.TextChanged += (_, _) =>
        {
            if (!_busy) ResetFlow();
        };
        Loaded += (_, _) => _ = RefreshInstanceAsync();
    }

    /// <summary>页面每次挂到树上都重读实例（缓存页复用，Loaded 会再次触发）。</summary>
    protected override void OnInitialized(EventArgs e)
    {
        // Loaded 挂接在构造函数里完成；这里保留覆写点即可。
        base.OnInitialized(e);
    }

    // ─────────────────────────── 实例状态 ───────────────────────────

    private async Task RefreshInstanceAsync()
    {
        _config = QlConfig.Load(App.DataDir);
        var dir = _config.GameDir;

        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            _config.GameDir = null;
            InstanceTitle.Text = "未选择游戏目录";
            InstanceDetail.Text = "先指定 QL2 实例目录（含 mods/ 的那个文件夹），再开始同步。";
            ResetFlow();
            return;
        }

        InstanceTitle.Text = dir;
        _state = QlSyncState.Load(dir);
        var marker = QlDirectoryLayout.DetectActiveChannel(dir, _state.ActiveChannel);
        var channel = marker ?? _state.ActiveChannel;
        InstanceDetail.Text = $"整合包版本 {_state.PackVersion ?? "（尚未同步）"} · 频道 {channel}";

        ManifestUrlBox.Text = string.IsNullOrWhiteSpace(_state.ManifestUrl)
            ? QlConstants.DefaultManifestUrl
            : _state.ManifestUrl;
        ResetFlow();
        await Task.CompletedTask;
    }

    private void ResetFlow()
    {
        _fetched = null;
        _manifestSourceUrl = null;
        OptionalHost.Children.Clear();
        OptionalTitle.Visibility = Visibility.Collapsed;
        BtnApply.IsEnabled = false;
        SyncResult.Text = "";
        SyncBar.Visibility = Visibility.Collapsed;
    }

    // ─────────────────────────── 目录与 URL ───────────────────────────

    private void OnPickGameDir(object sender, RoutedEventArgs e)
    {
        var dir = Pickers.Folder(Window.GetWindow(this));
        if (dir is null) return;
        try
        {
            _config.GameDir = dir;
            _config.Save(App.DataDir);
        }
        catch (Exception ex)
        {
            Globals.Log("游戏目录保存失败: " + ex.Message);
        }
        _ = RefreshInstanceAsync();
    }

    private void OnResetUrl(object sender, RoutedEventArgs e)
        => ManifestUrlBox.Text = QlConstants.DefaultManifestUrl;

    // ─────────────────────────── 查更新（fetch + analyze） ───────────────────────────

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireGameDir()) return;
        var dir = _config.GameDir!;
        var url = EffectiveUrl();

        SetBusy(true, indeterminate: true);
        SyncResult.Text = "正在拉取 manifest…";
        try
        {
            var manifest = await Task.Run(() => QlManifestFetcher.FetchAsync(url)).ConfigureAwait(true);
            _fetched = manifest;
            _manifestSourceUrl = url;
            BuildOptionalCheckboxes(manifest);

            SyncResult.Text = "正在对账文件（SHA-1）…";
            var state = _state!;
            var channel = SelectedChannel();
            var toggles = CollectedToggles();
            var analysis = await Task.Run(() =>
                QlSyncManager.Analyze(dir, manifest, channel, toggles)).ConfigureAwait(true);

            BtnApply.IsEnabled = true;
            SyncResult.Text = analysis.IsIdle
                ? $"已是最新：{manifest.PackNameOrDefault()} {manifest.PackVersionOrDefault()}（无需操作）。"
                : DescribeAnalysis(manifest, analysis);
        }
        catch (Exception ex)
        {
            _fetched = null;
            BtnApply.IsEnabled = false;
            Globals.Log("查更新失败: " + ex);
            SyncResult.Text = "查更新失败：" + FriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private string DescribeAnalysis(QlManifest manifest, QlSyncAnalysis a)
    {
        var lines = new List<string>
        {
            $"对照 {manifest.PackNameOrDefault()} {manifest.PackVersionOrDefault()}：",
        };
        if (a.ChannelSwitchPending)
            lines.Add($"· 频道切换 {a.FromChannel} → {a.ToChannel}");
        if (a.NeedDownload.Count > 0)
            lines.Add($"· 需下载 {a.NeedDownload.Count} 个文件");
        if (a.RenamesPending > 0)
            lines.Add($"· 将重命名 {a.RenamesPending} 个（可选组开关的改名，免下载）");
        if (a.DeletesPending > 0)
            lines.Add($"· 将清理 {a.DeletesPending} 个旧文件");
        return string.Join("\n", lines);
    }

    // ─────────────────────────── 应用同步 ───────────────────────────

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy || !RequireGameDir() || _fetched is null) return;
        var dir = _config.GameDir!;
        var manifest = _fetched;
        var url = _manifestSourceUrl ?? EffectiveUrl();
        var channel = SelectedChannel();
        var toggles = CollectedToggles();
        await RunSyncAsync(dir, manifest, channel, toggles, url);
    }

    private async Task RunSyncAsync(string dir, QlManifest manifest, string channel,
        Dictionary<string, bool>? toggles, string? url)
    {
        SetBusy(true);
        SyncResult.Text = "正在同步…";
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var progress = new Progress<(int Finished, int Total)>(p =>
            {
                if (p.Total > 0)
                {
                    SyncBar.Visibility = Visibility.Visible;
                    SyncBar.IsIndeterminate = false;
                    SyncBar.Value = 100.0 * p.Finished / p.Total;
                }
            });
            var outcome = await Task.Run(() =>
                QlSyncManager.Sync(dir, manifest, channel, toggles,
                    (f, t) => ((IProgress<(int, int)>)progress).Report((f, t)),
                    cts.Token, url)).ConfigureAwait(true);

            if (outcome.Canceled)
            {
                SyncResult.Text = "已取消（部分文件可能已就位，下次查更新会对账）。";
            }
            else if (outcome.FailedFiles.Count > 0)
            {
                var shown = string.Join("、", outcome.FailedFiles.Take(5));
                var more = outcome.FailedFiles.Count > 5 ? " 等" : "";
                SyncResult.Text =
                    $"部分失败：下载 {outcome.Downloaded} 个 · {shown}{more}（共 {outcome.FailedFiles.Count} 个）。\n" +
                    "可以是网络问题（服务器/CDN 抖动）；修好网络后再点一次「应用同步」即可续传补齐。";
            }
            else
            {
                SyncResult.Text =
                    $"同步完成：下载 {outcome.Downloaded} · 未变 {outcome.Unchanged}" +
                    (outcome.Renamed > 0 ? $" · 重命名 {outcome.Renamed}" : "") +
                    (outcome.Deleted > 0 ? $" · 清理 {outcome.Deleted}" : "") +
                    $" → {outcome.PackVersion}";
            }
        }
        catch (OperationCanceledException)
        {
            SyncResult.Text = "已取消。";
        }
        catch (Exception ex)
        {
            Globals.Log("同步失败: " + ex);
            SyncResult.Text = "同步失败：" + FriendlyMessage(ex);
        }
        finally
        {
            if (_cts == cts) _cts = null;
            cts.Dispose();
            SetBusy(false);
            await RefreshInstanceAsync().ConfigureAwait(true);
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ─────────────────────────── 种子导入 ───────────────────────────

    private void OnSeedBrowse(object sender, RoutedEventArgs e)
    {
        var path = Pickers.Mrpack(Window.GetWindow(this));
        if (path is null) return;
        SeedPathBox.Text = path;
        BtnSeedImport.IsEnabled = true;
        SeedStatus.Text = "";
    }

    private async void OnSeedImport(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dir = _config.GameDir;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            SeedStatus.Text = "先「选择目录…」指定 QL2 实例目录。";
            return;
        }
        var mrpack = SeedPathBox.Text.Trim();
        if (mrpack.Length == 0 || !File.Exists(mrpack))
        {
            SeedStatus.Text = "先「浏览…」选择 .mrpack 种子包。";
            return;
        }

        SetBusy(true, seedFlow: true);
        SeedBar.Visibility = Visibility.Visible;
        SeedBar.IsIndeterminate = false;
        try
        {
            var head = await Task.Run(() => QlSeedImporter.ReadHead(mrpack)).ConfigureAwait(true);
            SeedStatus.Text =
                $"正在安装「{head.Name} {QlSeedImporter.NormalizeVersionId(head.VersionId)}」" +
                (head.Minecraft is null ? "" : $"（MC {head.Minecraft} · fabric {head.FabricLoader}）") + "…";
            await Task.Delay(50); // 让状态先渲染

            var install = await Task.Run(() =>
                QlSeedImporter.InstallMrpackAsync(mrpack, dir,
                    SeedListener(), CancellationToken.None)).ConfigureAwait(true);

            var manifestUrl = string.IsNullOrWhiteSpace(ManifestUrlBox.Text)
                ? QlConstants.DefaultManifestUrl
                : ManifestUrlBox.Text.Trim();
            var manifest = await Task.Run(() => QlManifestFetcher.FetchAsync(manifestUrl)).ConfigureAwait(true);

            SeedStatus.Text = "正在 QL 化（对账 + 布局盖章）…";
            await Task.Delay(50);

            var seeded = await Task.Run(() =>
                QlSeedImporter.Seedize(dir, manifest, manifestUrl,
                    targetChannel: "std",
                    optionalExtras: null,
                    listener: SeedListener(),
                    CancellationToken.None)).ConfigureAwait(true);

            SeedStatus.Text =
                $"导入完成：{install.OverridesCopied} 个整合包文件 + {install.FilesDownloaded} 个外部文件落地，" +
                $"种子对账 seeded {seeded.Seeded} · 缺件 {seeded.Missing} · 不匹配 {seeded.Mismatch}。" +
                (seeded.Missing > 0 || seeded.Mismatch > 0
                    ? "点「查更新」→「应用同步」补齐缺件即可入坑。"
                    : "整包已就位，可启动。");
            await RefreshInstanceAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Globals.Log("种子导入失败: " + ex);
            SeedStatus.Text = "导入失败：" + FriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false, seedFlow: true);
        }
    }

    /// <summary>种子导入进度桥：引擎在后台线程回调，这里投回 UI 线程改进度条。</summary>
    private Action<int, int> SeedListener() => (f, t) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (t <= 0) return;
            SeedBar.Visibility = Visibility.Visible;
            SeedBar.IsIndeterminate = false;
            SeedBar.Value = Math.Min(100, 100.0 * f / t);
        });

    // ─────────────────────────── 杂项 ───────────────────────────

    private bool RequireGameDir()
    {
        if (!string.IsNullOrWhiteSpace(_config.GameDir)) return true;
        SyncResult.Text = "先「选择目录…」指定 QL2 实例目录。";
        return false;
    }

    private string EffectiveUrl()
    {
        var url = ManifestUrlBox.Text.Trim();
        return url.Length > 0 ? url : QlConstants.DefaultManifestUrl;
    }

    private string SelectedChannel() => ChannelPfm.IsChecked == true ? "pfm" : "std";

    /// <summary>读可选组勾选；没有勾选界面（未拉 manifest）时返回 null，语义=沿用状态文件。</summary>
    private Dictionary<string, bool>? CollectedToggles()
    {
        if (OptionalHost.Children.Count == 0) return null;
        var toggles = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var child in OptionalHost.Children)
            if (child is CheckBox { Tag: string group } cb)
                toggles[group] = cb.IsChecked == true;
        return toggles;
    }

    private void BuildOptionalCheckboxes(QlManifest manifest)
    {
        OptionalHost.Children.Clear();
        var groups = manifest.OptionalGroups();
        if (groups.Count == 0)
        {
            OptionalTitle.Visibility = Visibility.Collapsed;
            return;
        }
        OptionalTitle.Visibility = Visibility.Visible;
        foreach (var group in groups)
        {
            var cb = new CheckBox
            {
                Content = group,
                Tag = group,
                IsChecked = _state?.IsOptionalEnabled(group) ?? false,
                Style = (Style)FindResource("OptionCheck"),
                Margin = new Thickness(0, 2, 0, 2),
            };
            OptionalHost.Children.Add(cb);
        }
    }

    private void SetBusy(bool busy, bool indeterminate = false, bool seedFlow = false)
    {
        _busy = busy;
        BtnCheck.IsEnabled = !busy;
        BtnSeedBrowse.IsEnabled = !busy;
        BtnSeedImport.IsEnabled = !busy && SeedPathBox.Text.Trim().Length > 0;
        BtnApply.IsEnabled = !busy && _fetched is not null;
        BtnCancel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!seedFlow)
        {
            SyncBar.Visibility = busy || indeterminate ? Visibility.Visible : Visibility.Collapsed;
            SyncBar.IsIndeterminate = busy && indeterminate;
        }
        MainWindow.Instance.SetStatus(busy ? "轻蓝同步进行中…" : "准备就绪");
    }

    private static string FriendlyMessage(Exception e)
        => e is ManifestException me ? me.Message : e.Message;
}
