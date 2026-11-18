using System.IO;
using System.Windows;
using System.Windows.Controls;
using Qinglan.Launcher.Core;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>轻蓝同步页。语义对齐 QL-HMCL 新版 QLSyncPage：更新源就是整合包自己的
/// .mrpack（现场挑选，或实例内 .ql-pack 缓存最新包）；查更新 = 读包版本 + 可选组，
/// 应用 = QlPackSync.Sync。实例固定为游戏根目录下的 Qinglan-2nd-RPU（版本隔离感知），
/// 只有携带 RPU mod 的实例允许更新；RPU 区域暂留半空。</summary>
public partial class SyncPage : UserControl
{
    private QlConfig _config = new();
    private QlSyncState? _state;
    private string? _gameRoot;
    private string? _runDir;
    private bool _syncable;
    private string? _pickedPack;
    private bool _busy;

    public SyncPage()
    {
        InitializeComponent();
        Loaded += (_, _) => _ = RefreshInstanceAsync();
    }

    // ─────────────────────────── 实例状态 ───────────────────────────

    /// <summary>页面每次挂到树上都重读实例（缓存页复用，Loaded 会再次触发）：
    /// 用户可能改过游戏根目录，陈旧的提示会把更新灌进错误的实例。</summary>
    /// <param name="keepResult">为 true 时保留同步结果文本与进度条——应用成功的摘要
    /// 不能被紧跟着的刷新抹掉。</param>
    private async Task RefreshInstanceAsync(bool keepResult = false)
    {
        _config = QlConfig.Load(App.DataDir);
        _gameRoot = _config.GameDir;
        _pickedPack = null;
        PackPathBox.Text = "";

        if (string.IsNullOrWhiteSpace(_gameRoot) || !Directory.Exists(_gameRoot))
        {
            _config.GameDir = null;
            _gameRoot = null;
            _runDir = null;
            _syncable = false;
            InstanceTitle.Text = "未选择游戏根目录";
            InstanceDetail.Text = "先指定游戏根目录（.minecraft），启动器只管理其中的 Qinglan-2nd-RPU 实例（版本隔离）。";
            PackInfoLabel.Text = "";
            ApplyGate();
            ResetFlow();
            return;
        }

        _runDir = QlInstances.ResolveRunDir(_gameRoot);
        if (_runDir == null)
        {
            _syncable = false;
            InstanceTitle.Text = "找不到 Qinglan-2nd-RPU 实例";
            InstanceDetail.Text = _gameRoot + " 里没有 versions\\Qinglan-2nd-RPU，" +
                                  "也不像平铺的实例目录（缺 mods/ 与 QL 状态标记）。";
            PackInfoLabel.Text = "";
            ApplyGate();
            ResetFlow();
            return;
        }

        var isolated = QlInstances.IsIsolatedLayout(_gameRoot);
        InstanceTitle.Text = isolated ? "Qinglan-2nd-RPU（版本隔离）" : "Qinglan-2nd-RPU（平铺）";
        InstanceDetail.Text = _runDir;
        _state = QlSyncState.Load(_runDir);

        var rpuMod = QlPackGuard.FindRpuMod(_runDir);
        _syncable = rpuMod != null;
        if (_syncable)
            InstanceDetail.Text += "\nRPU mod: " + rpuMod;
        PackInfoLabel.Text = string.IsNullOrWhiteSpace(_state.PackUrl)
            ? ""
            : "已记录更新源：" + _state.PackUrl;
        ApplyGate();
        ResetFlow(keepResult);
        await Task.CompletedTask;
    }

    /// <summary>只有包自己的实例（mods 里有 RPU mod）允许更新；其余给解释而非半坏页面。</summary>
    private void ApplyGate()
    {
        if (_runDir == null)
        {
            GateLabel.Visibility = Visibility.Collapsed;
            return;
        }
        GateLabel.Text = _syncable
            ? ""
            : "该实例没有启用中的 RPU mod（qinglanrpuclient），不属于整合包管理范围，同步已锁定。";
        GateLabel.Visibility = _syncable ? Visibility.Collapsed : Visibility.Visible;
        if (!_syncable) ResetFlow();
    }

    /// <summary>清掉「查更新/选包」的临时态；<paramref name="keepResult"/> 保留摘要与进度条。</summary>
    private void ResetFlow(bool keepResult = false)
    {
        OptionalHost.Children.Clear();
        OptionalTitle.Visibility = Visibility.Collapsed;
        BtnApply.IsEnabled = false;
        if (!keepResult)
        {
            SyncResult.Text = "";
            SyncBar.Visibility = Visibility.Collapsed;
        }
    }

    // ─────────────────────────── 目录与包源 ───────────────────────────

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
            Globals.Log("游戏根目录保存失败: " + ex.Message);
        }
        _ = RefreshInstanceAsync();
    }

    /// <summary>本页使用的包文件：现场挑的优先，其次实例内 .ql-pack 缓存里最新的 .mrpack。
    /// 构建里不烘任何远端地址。</summary>
    private string? SelectedPack()
    {
        if (_pickedPack != null) return _pickedPack;
        var dir = _runDir;
        if (dir == null) return null;
        var cache = Path.Combine(dir, QlPackSync.CacheDir);
        if (!Directory.Exists(cache)) return null;
        try
        {
            return Directory.EnumerateFiles(cache, "*.mrpack", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch (IOException e)
        {
            Globals.Log("包缓存列不动: " + cache + " -> " + e.Message);
            return null;
        }
    }

    private void OnPickPack(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var path = Pickers.Mrpack(Window.GetWindow(this));
        if (path is null) return;
        _pickedPack = path;
        PackPathBox.Text = path;
        SyncResult.Text = "";
        _ = CheckPackUpdateAsync();
    }

    // ─────────────────────────── 查更新 ───────────────────────────

    private async void OnCheckUpdate(object sender, RoutedEventArgs e)
    {
        if (_busy || _runDir == null)
        {
            if (_runDir == null)
                SyncResult.Text = "先「选择游戏根目录…」定位 Qinglan-2nd-RPU 实例。";
            return;
        }

        var pack = SelectedPack();
        if (pack == null)
        {
            // HMCL 同款：没有包就现场挑，不空转报错。
            OnPickPack(sender, e);
            return;
        }

        await CheckPackUpdateAsync();
    }

    /// <summary>读所选包并对照实例状态：版本差距 + 可选组勾选（对齐 QL-HMCL checkPackUpdate）。</summary>
    private async Task CheckPackUpdateAsync()
    {
        if (_busy || _runDir == null) return;
        var dir = _runDir;
        var pack = SelectedPack();
        if (pack == null) return;

        SetBusy(true);
        SyncResult.Text = "正在读取整合包…";
        try
        {
            var current = await Task.Run(() =>
            {
                var version = QlPackSync.ReadPackVersion(pack);
                var groups = QlPackSync.OptionalGroups(pack);
                var state = QlSyncState.Load(dir);
                return (Version: version, Groups: groups, State: state);
            }).ConfigureAwait(true);

            _state = current.State;
            var now = current.State.PackVersion is { Length: > 0 } v ? v : "?";
            PackInfoLabel.Text = $"当前实例 {now} → 包 {current.Version}";

            BuildOptionalCheckboxes(current.Groups);

            SyncResult.Text = now.Equals(current.Version, StringComparison.Ordinal)
                ? $"已是最新：{current.Version}（无需操作）。"
                : "可应用同步。注意：可选组勾选即刻生效（停用的 mod 会在应用时停成 .disabled）";
            BtnApply.IsEnabled = true;
        }
        catch (Exception ex)
        {
            BtnApply.IsEnabled = false;
            Globals.Log("查更新失败: " + ex);
            SyncResult.Text = "查更新失败：" + FriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // ─────────────────────────── 应用同步 ───────────────────────────

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (_busy || _runDir == null || !_syncable)
        {
            if (_runDir != null && !_syncable)
                SyncResult.Text = "该实例没有启用中的 RPU mod（qinglanrpuclient），不属于整合包管理范围。";
            return;
        }
        var dir = _runDir;
        var pack = SelectedPack();
        if (pack == null)
        {
            SyncResult.Text = "先「浏览…」选择 .mrpack 整合包。";
            return;
        }

        SetBusy(true);
        SyncResult.Text = "正在同步…";
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
                QlPackSync.Sync(dir, pack, null,
                    (f, t) => ((IProgress<(int, int)>)progress).Report((f, t)))).ConfigureAwait(true);

            var summary =
                $"同步完成：下载 {outcome.Downloaded} · 未变 {outcome.Unchanged}" +
                (outcome.Renamed > 0 ? $" · 重命名 {outcome.Renamed}" : "") +
                (outcome.Deleted > 0 ? $" · 清理 {outcome.Deleted}" : "");
            if (outcome.HandedOver > 0)
                summary += $"\n交接给游戏内 RPU 客户端 {outcome.HandedOver} 个文件（不删）";
            if (outcome.FailedPaths.Count > 0)
            {
                var shown = string.Join("、", outcome.FailedPaths.Take(5));
                var more = outcome.FailedPaths.Count > 5 ? " 等" : "";
                summary += $"\n部分失败（{outcome.Failed} 个）：{shown}{more}。修好网络后再点一次「应用同步」即可续传补齐。";
            }
            SyncResult.Text = summary;
        }
        catch (Exception ex)
        {
            Globals.Log("同步失败: " + ex);
            SyncResult.Text = "同步失败：" + FriendlyMessage(ex);
        }
        finally
        {
            SetBusy(false);
            // 保留摘要：应用结果（下载/未变/失败续传提示）是用户唯一能看到同步结果的地方。
            await RefreshInstanceAsync(keepResult: true).ConfigureAwait(true);
        }
    }

    // ─────────────────────────── 可选组 ───────────────────────────

    /// <summary>勾选即落盘（对齐 HMCL toggleOptionalGroup）：下次应用时停用/还原对应 mod。</summary>
    private void OnOptionalToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string group } cb) return;
        var state = _state;
        var dir = _runDir;
        if (state == null || dir == null) return;
        state.SetOptionalEnabled(group, cb.IsChecked == true);
        _ = Task.Run(() =>
        {
            try
            {
                state.Save(dir);
            }
            catch (IOException ex)
            {
                Globals.Log("可选组状态保存失败: " + ex.Message);
            }
        });
    }

    private void BuildOptionalCheckboxes(IReadOnlyList<string> groups)
    {
        OptionalHost.Children.Clear();
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
            cb.Checked += OnOptionalToggle;
            cb.Unchecked += OnOptionalToggle;
            OptionalHost.Children.Add(cb);
        }
    }

    // ─────────────────────────── RPU 接口挂点 ───────────────────────────

    /// <summary>RPU 更新触发接口：RPU 客户端为闭源组件，启动器不实现其更新逻辑；
    /// 外部组件接入时订阅此事件并负责真正的更新流程。当前无人订阅，按钮保持禁用。</summary>
    public event EventHandler? RpuUpdateRequested;

    /// <summary>RPU 进度投影接口：外部组件上报（状态文本, 0–100 进度；null = 隐藏进度条），
    /// 启动器只负责把它投影到 RPU 卡片上。</summary>
    public void SetRpuUpdateState(string? status, double? percent)
    {
        RpuStatusText.Text = string.IsNullOrWhiteSpace(status)
            ? "（RPU 接口预留：更新状态 · 发布清单 · 手动刷新）"
            : status;
        if (percent is { } p)
        {
            RpuProgressBar.Visibility = Visibility.Visible;
            RpuProgressBar.Value = Math.Clamp(p, 0, 100);
        }
        else
        {
            RpuProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>接口预留按钮：接线后由订阅方接管；未接线时按钮禁用，此处只做防御。</summary>
    private void OnRpuUpdateClick(object sender, RoutedEventArgs e)
        => RpuUpdateRequested?.Invoke(this, EventArgs.Empty);

    // ─────────────────────────── 杂项 ───────────────────────────

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BtnCheck.IsEnabled = !busy;
        BtnPackBrowse.IsEnabled = !busy;
        BtnApply.IsEnabled = !busy && _syncable && SelectedPack() != null;
        MainWindow.Instance.SetStatus(busy ? "轻蓝同步进行中…" : "准备就绪");
    }

    private static string FriendlyMessage(Exception e)
        => e is ManifestException me ? me.Message : e.Message;
}
