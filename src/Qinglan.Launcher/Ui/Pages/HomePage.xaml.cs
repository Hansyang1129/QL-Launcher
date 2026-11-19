using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Auth;
using Qinglan.Launcher.Core.Diagnostics;
using Qinglan.Launcher.Core.Launch;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>
/// 首页 = 启动台：实例摘要、账号（离线 / 微软设备码）、内存与 Java、启动/取消/结束、
/// 以及右侧滚动的启动日志。启动链复用 M2a 引擎（合并包已由整合包提供为单文件版本 JSON）。
/// </summary>
public partial class HomePage : UserControl
{
    private static readonly Random Rng = new();
    private readonly List<int> _memoryValues = new();
    private readonly Queue<string> _recentLog = new();
    private string? _lastDiagnosisFingerprint;

    private QlConfig _config = new();
    private string? _gameRoot;
    private string? _runDir;
    private QlVersionJson? _version;
    private QlJavaRuntime? _java;
    private QlMsAccount? _msAccount;
    private QlGameProcess? _game;
    private CancellationTokenSource? _prepCts;
    private CancellationTokenSource? _loginCts;
    private Task? _msRestoreTask;
    private bool _msRefreshTried;
    private bool _msRestoreFailed;
    private string? _restoredForClientId;
    private bool _busy;
    private readonly QlLogXml _logXml = new();
    private const string LogPlaceholder = "（启动器与游戏输出会显示在这里）";
    private bool _loadingUi;
    private int _javaProbeGen;
    private string _verifyUri = "";

    public HomePage()
    {
        InitializeComponent();
        TryLoadWallpaper();
        Loaded += (_, _) => Refresh();
    }

    // ─────────────────────────── 实例与账号状态 ───────────────────────────

    /// <summary>每次页面挂回树上都重读配置/实例（设置页可能刚改过根目录或 Java）。</summary>
    private void Refresh()
    {
        _config = QlConfig.Load(App.ConfigDir);
        _gameRoot = string.IsNullOrWhiteSpace(_config.GameDir) ? null : _config.GameDir;
        _runDir = _gameRoot is not null && Directory.Exists(_gameRoot)
            ? QlInstances.ResolveRunDir(_gameRoot)
            : null;
        _version = null;

        string gate = "";
        if (_gameRoot is null) gate = "还没有选择游戏根目录。";
        else if (!Directory.Exists(_gameRoot)) gate = "游戏根目录不存在：" + _gameRoot;
        else if (_runDir is null) gate = "根目录里找不到 Qinglan-2nd-RPU 实例。";
        else
        {
            var json = Path.Combine(_runDir, QlInstances.ManagedInstanceId + ".json");
            if (!File.Exists(json))
                gate = "实例缺少版本文件 " + Path.GetFileName(json) + "，先完成一次整合包同步。";
            else
                try
                {
                    _version = QlVersionJson.Parse(File.ReadAllText(json));
                }
                catch (Exception ex)
                {
                    gate = "版本文件解析失败：" + ex.Message;
                    Globals.Log("版本 JSON 读取失败: " + ex);
                }
        }

        // 头部信息：包名/版本 + 实例布局。
        var state = _runDir is null ? null : QlSyncState.Load(_runDir);
        var dbl = _runDir is null ? null : QlDblPack.Load(_runDir);
        var packName = dbl?.Pack?.Name ?? Globals.PackName;
        var packVersion = dbl?.Pack?.Version ?? state?.PackVersion;
        PackInfo.Text = string.IsNullOrWhiteSpace(packVersion) ? packName : packName + " · " + packVersion;
        InstanceInfo.Text = _runDir is null
            ? "尚未连接实例 · 先到「轻蓝同步」导入整合包"
            : (QlInstances.IsIsolatedLayout(_gameRoot!) ? "版本隔离 · " : "平铺 · ") + _runDir;

        _loadingUi = true;
        try
        {
            PlayerNameBox.Text = (_config.PlayerName ?? "").Trim();
            var memoryStatus = QlMemory.ReadStatus();
            int autoMb = QlMemory.AutoMb(QlMemory.CountMods(_runDir), memoryStatus);
            RebuildMemoryChoices(autoMb);
        }
        finally
        {
            _loadingUi = false;
        }

        if (!_busy && _game is null)
        {
            LaunchStatus.Text = "";
            LaunchBar.Visibility = Visibility.Collapsed;
        }

        LaunchHint.Text = gate.Length > 0
            ? gate + " 先到「轻蓝同步」完成一次包应用，再回来启动。"
            : (_version is not null
                ? "启动前会自动补齐缺失的启动文件；首次运行可能需要下载 Java " + _version.JavaMajor + " 运行时。"
                : "");
        UpdateControls();
        ApplyAccountUi();
        _ = ProbeJavaAsync(_config.JavaDir);

        // client id 可能刚在设置页填上：换过 id 就允许重新尝试静默恢复。
        if (!string.Equals(_restoredForClientId, _config.MicrosoftClientId, StringComparison.Ordinal))
        {
            _restoredForClientId = _config.MicrosoftClientId;
            _msRefreshTried = false;
        }
        TryRestoreMicrosoft();
    }

    private int NeedJavaMajor => _version is { JavaMajor: > 0 } v ? v.JavaMajor : QlJavaLocator.DefaultMinMajor;

    /// <summary>首页只展示 Home 目录名（如 jdk-21），完整 java.exe 路径在悬停提示里。</summary>
    private static string JavaFolderName(QlJavaRuntime java)
    {
        try
        {
            return Path.GetFileName(java.HomeDir.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch
        {
            return java.Version;
        }
    }

    private async Task ProbeJavaAsync(string? javaDir)
    {
        int gen = ++_javaProbeGen;
        int need = NeedJavaMajor;
        JavaText.Text = "探测中…";
        try
        {
            var found = await Task.Run(() => QlJavaLocator.FindBest(javaDir, need));
            if (gen != _javaProbeGen) return;
            _java = found;
            if (found is null)
            {
                JavaText.Text = "未找到 Java " + need + "+（启动时自动下载）";
                JavaText.ToolTip = "会自动下载 Mojang 官方 Java 运行时到启动器 data/runtimes/";
            }
            else
            {
                JavaText.Text = "Java " + found.MajorVersion + " · " + JavaFolderName(found);
                JavaText.ToolTip = found.ExePath;
            }
        }
        catch (Exception ex)
        {
            if (gen != _javaProbeGen) return;
            JavaText.Text = "Java 探测失败：" + ex.Message;
            Globals.Log("Java 探测失败: " + ex);
        }
    }

    // ─────────────────────────── 微软账号 ───────────────────────────

    private void ApplyAccountUi()
    {
        bool logged = _msAccount is not null;
        bool configured = !string.IsNullOrWhiteSpace(_config.MicrosoftClientId);
        AccountState.Text = logged ? "微软账号" : "离线模式";
        AccountState.Foreground = (Brush)FindResource(logged ? "OkGreen" : "TextSecondary");
        OfflinePanel.Visibility = logged ? Visibility.Collapsed : Visibility.Visible;
        MsPanel.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
        if (logged)
        {
            MsAccountText.Text = "已登录：" + _msAccount!.Name;
            AccountHint.Text = "启动时会自动续期微软令牌；令牌失效会提示重新登录。";
        }
        else if (!configured)
        {
            AccountHint.Text = "微软登录未启用：在「设置」填写 Azure 应用 client id 后即可登录正版账号；离线模式填写玩家名后可启动。";
        }
        else if (_msRestoreFailed)
        {
            AccountHint.Text = "微软登录已失效：点右侧重新登录，或先「退出登录」再用离线账号启动。";
        }
        else
        {
            AccountHint.Text = "离线模式填写玩家名后启动；正版账号点右侧登录（设备码，无需密码输入到启动器）。";
        }
        BtnMsLogin.ToolTip = configured ? null : "先在「设置」里填写微软应用 client id";
        UpdateControls();
    }

    private void TryRestoreMicrosoft()
    {
        if (_msRefreshTried) return;
        _msRefreshTried = true;
        if (_config.MicrosoftRefreshToken is not { Length: > 0 } refresh ||
            _config.MicrosoftClientId is not { Length: > 0 } clientId)
            return;

        AccountState.Text = "正在恢复微软登录…";
        _msRestoreTask = RestoreMicrosoftAsync(clientId, refresh);
    }

    private async Task RestoreMicrosoftAsync(string clientId, string refreshToken)
    {
        try
        {
            var account = await new QlMicrosoftAuth().RefreshAsync(clientId, refreshToken);
            _msAccount = account;
            _msRestoreFailed = false;
            PersistMicrosoft(account);
            Ui(() =>
            {
                ApplyAccountUi();
                AppendLog("[启动器] 微软登录已恢复：" + account.Name);
            });
        }
        catch (Exception ex)
        {
            _msRestoreFailed = true;
            Globals.Log("微软登录恢复失败: " + ex.Message);
            Ui(() =>
            {
                ApplyAccountUi();
                AppendLog("[启动器] 微软登录恢复失败：" + ex.Message);
            });
        }
        finally
        {
            _msRestoreTask = null;
        }
    }

    private async void OnMsLogin(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var clientId = _config.MicrosoftClientId?.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            AccountHint.Text = "先在「设置」里填写微软应用 client id。";
            return;
        }

        _loginCts = new CancellationTokenSource();
        _verifyUri = "";
        UserCodeText.Text = "……";
        BtnOpenVerify.IsEnabled = false;
        LoginStatusText.Text = "正在申请设备码…";
        LoginOverlay.Visibility = Visibility.Visible;
        SetBusy(true);
        LaunchStatus.Text = "等待微软授权…";
        try
        {
            var account = await new QlMicrosoftAuth().SignInAsync(clientId, (code, uri) => Ui(() =>
            {
                _verifyUri = uri;
                UserCodeText.Text = code;
                BtnOpenVerify.IsEnabled = true;
                LoginStatusText.Text = "在浏览器打开登录页并输入上方代码，授权完成后窗口会自动关闭。";
            }), _loginCts.Token);

            _msAccount = account;
            _msRestoreFailed = false;
            PersistMicrosoft(account);
            LoginOverlay.Visibility = Visibility.Collapsed;
            ApplyAccountUi();
            AppendLog("[启动器] 微软登录成功：" + account.Name);
        }
        catch (OperationCanceledException)
        {
            LoginOverlay.Visibility = Visibility.Collapsed;
            AppendLog("[启动器] 已取消微软登录。");
        }
        catch (Exception ex)
        {
            LoginStatusText.Text = FriendlyMessage(ex);
            Globals.Log("微软登录失败: " + ex);
        }
        finally
        {
            _loginCts?.Dispose();
            _loginCts = null;
            SetBusy(false);
        }
    }

    private void OnCloseLogin(object sender, RoutedEventArgs e)
    {
        _loginCts?.Cancel();
        LoginOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnCopyCode(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(UserCodeText.Text) && UserCodeText.Text != "……")
                Clipboard.SetText(UserCodeText.Text);
        }
        catch (Exception ex)
        {
            Globals.Log("复制设备码失败: " + ex.Message);
        }
    }

    private void OnOpenVerify(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_verifyUri)) Globals.OpenUrl(_verifyUri);
    }

    private void OnLogout(object sender, RoutedEventArgs e)
    {
        _msAccount = null;
        _msRestoreFailed = false;
        _config.MicrosoftRefreshToken = null;
        _config.MicrosoftAccountName = null;
        SaveConfigQuiet();
        ApplyAccountUi();
        AppendLog("[启动器] 已退出微软登录，回到离线模式。");
    }

    private void PersistMicrosoft(QlMsAccount account)
    {
        _config.MicrosoftRefreshToken = account.MsRefreshToken;
        _config.MicrosoftAccountName = account.Name;
        SaveConfigQuiet();
    }

    // ─────────────────────────── 启动流程 ───────────────────────────

    private async void OnLaunch(object sender, RoutedEventArgs e) => await LaunchAsync();

    private async Task LaunchAsync()
    {
        if (_busy || _game is { HasExited: false }) return;
        if (_runDir is null || _version is null)
        {
            LaunchHint.Text = "实例尚未就绪：先去「轻蓝同步」完成一次包应用。";
            UpdateControls();
            return;
        }

        // 离线模式不提供默认玩家名：未登录微软且输入为空时先提示填写，避免静默使用假名。
        bool msConfigured = _config.MicrosoftRefreshToken is { Length: > 0 } &&
                            _config.MicrosoftClientId is { Length: > 0 };
        if (_msAccount is null && !msConfigured && string.IsNullOrWhiteSpace(PlayerNameBox.Text))
        {
            LaunchStatus.Text = "请先填写离线玩家名。";
            LaunchHint.Text = "离线启动需要 1–16 位玩家名；填写后即可启动。";
            PlayerNameBox.Focus();
            return;
        }

        _prepCts = new CancellationTokenSource();
        var ct = _prepCts.Token;
        ShowDiagnosis(null);
        SetBusy(true);
        LaunchBar.Visibility = Visibility.Visible;
        LaunchBar.IsIndeterminate = true;
        LaunchBar.Value = 0;
        LaunchStatus.Text = "正在准备启动…";
        LaunchHint.Text = "准备期间可点「取消准备」中止；下载已完成的文件会保留。";
        bool launched = false;
        try
        {
            // 微软登录恢复可能还在路上：等它结束再决定账号形态。
            if (_msRestoreTask is { } wait) await wait;

            var java = await EnsureJavaAsync(ct);
            _java = java;
            Ui(() =>
            {
                JavaText.Text = "Java " + java.MajorVersion + " · " + JavaFolderName(java);
                JavaText.ToolTip = java.ExePath;
            });

            var gameRoot = _gameRoot!;
            var runDir = _runDir;
            var infra = string.Equals(Path.GetFullPath(runDir), Path.GetFullPath(gameRoot),
                StringComparison.OrdinalIgnoreCase) ? runDir : gameRoot;

            Ui(() =>
            {
                LaunchBar.IsIndeterminate = true;
                LaunchStatus.Text = "正在检查启动文件…";
            });
            var tasks = await QlLaunchPrep.PlanAsync(_version, infra, ct: ct);
            if (tasks.Count > 0)
            {
                var head = string.Join("、", tasks.Take(4).Select(t => t.Label));
                AppendLog("[启动器] 需要补齐 " + tasks.Count + " 个启动文件：" + head +
                          (tasks.Count > 4 ? " …" : ""));
                await QlLaunchPrep.DownloadAllAsync(tasks, (done, total) =>
                    Ui(() => SetProgress(done, total, "正在下载启动文件")), ct);
            }
            else
            {
                AppendLog("[启动器] 启动文件已就绪。");
            }

            Ui(() =>
            {
                LaunchBar.IsIndeterminate = true;
                LaunchStatus.Text = "正在装配 natives 与启动参数…";
            });
            var prep = await Task.Run(() => QlLaunchPrep.Finalize(_version, infra,
                Path.Combine(runDir, "natives-windows")), ct);

            var profile = await ResolveProfileAsync(ct);
            var memoryStatus = QlMemory.ReadStatus();
            var memory = QlMemory.EffectiveMb(_config.MaxMemoryMb, memoryStatus,
                QlMemory.CountMods(runDir));
            int finalMemory = memory;
            bool xmxOverridden = false;
            if (QlJvmOptions.TryParse(_config.JvmArgs, memory, out var jvmPlan, out _))
            {
                finalMemory = jvmPlan.XmxMb;
                xmxOverridden = jvmPlan.UserProvidedXmx;
            }
            var args = QlArgsBuilder.Build(prep, runDir, profile, memory, _config.JvmArgs,
                _config.UseVersionJvmArgs);

            AppendLog("[启动器] Java " + java.MajorVersion + " · 内存 " + finalMemory + " MB"
                      + (xmxOverridden ? "（-Xmx 由额外 JVM 参数覆盖）" : "") + " · 账号 " +
                      profile.Name + "（" + profile.UserType + "）");
            AppendLog("[启动器] 主类 " + prep.MainClass + " · 类路径 " + prep.Classpath.Count +
                      " 段 · 实例 " + runDir);

            if (_config.OptimizeMemoryBeforeLaunch)
            {
                Ui(() =>
                {
                    LaunchBar.IsIndeterminate = true;
                    LaunchStatus.Text = "正在优化内存…";
                });
                AppendLog("[启动器] 正在优化内存…");
                Globals.Log("内存优化开始");
                int trimmed = await Task.Run(() => QlMemoryOptimizer.TrimAll(3000), ct);
                Globals.Log("内存优化完成: " + trimmed + " 个进程");
                AppendLog("[启动器] 内存优化完成，尝试整理 " + trimmed + " 个进程。");
            }

            _game = new QlGameProcess(java.ExePath, args, runDir, OnGameLine, OnGameExit);
            launched = true;
            LaunchBar.Visibility = Visibility.Collapsed;
            LaunchStatus.Text = "游戏已启动，日志持续输出中…";
            LaunchHint.Text = "游戏运行中。关闭启动器不会结束游戏；需要时可点「结束游戏」。";
        }
        catch (OperationCanceledException)
        {
            LaunchStatus.Text = "已取消启动准备。";
            AppendLog("[启动器] 已取消启动准备。");
        }
        catch (Exception ex)
        {
            LaunchStatus.Text = "启动失败：" + FriendlyMessage(ex);
            LaunchHint.Text = "已生成本地排查建议；可展开原始日志核对。";
            AppendLog("[启动器] 启动失败：" + ex.Message);
            Globals.Log("启动失败: " + ex);
            ShowDiagnosis(QlErrorDiagnoser.Diagnose(new QlErrorContext(
                -1, Array.Empty<string>(), _recentLog.ToArray(),
                QlLogTail.ReadTail(App.LogFile, 200), ex)));
        }
        finally
        {
            if (!launched) LaunchBar.Visibility = Visibility.Collapsed;
            _prepCts?.Dispose();
            _prepCts = null;
            SetBusy(false);
        }
    }

    /// <summary>找 Java；没有就按版本 JSON 的组件名下载 Mojang 官方运行时（进度投影到启动条）。</summary>
    private async Task<QlJavaRuntime> EnsureJavaAsync(CancellationToken ct)
    {
        int need = NeedJavaMajor;
        Ui(() =>
        {
            LaunchBar.IsIndeterminate = true;
            LaunchStatus.Text = "正在探测 Java " + need + "+…";
        });
        var found = await Task.Run(() => QlJavaLocator.FindBest(_config.JavaDir, need), ct);
        if (found is not null) return found;

        var component = string.IsNullOrWhiteSpace(_version?.JavaComponent)
            ? "java-runtime-gamma"
            : _version!.JavaComponent!;
        Ui(() =>
        {
            LaunchBar.IsIndeterminate = true;
            LaunchStatus.Text = "缺少 Java，正在获取运行时清单…";
        });
        var (versionName, manifestUrl) = await QlMojangRuntime.ResolveAsync(component, need, ct: ct);
        AppendLog("[启动器] 未找到 Java " + need + "+，开始下载 Mojang 运行时 " + versionName + "。");
        var home = await QlMojangRuntime.InstallAsync(component, manifestUrl,
            Path.Combine(App.DataDir, "runtimes", component),
            (done, total) => Ui(() => SetProgress(done, total, "正在下载 Java 运行时")), ct);

        var exe = Path.Combine(home, "bin", "java.exe");
        var java = QlJavaLocator.TryParse(exe)
            ?? throw new IOException("Java 运行时下载完成但无法执行：" + exe);
        AppendLog("[启动器] Java 运行时就绪：" + exe);
        return java;
    }

    private async Task<QlProfile> ResolveProfileAsync(CancellationToken ct)
    {
        bool msConfigured = _config.MicrosoftRefreshToken is { Length: > 0 } &&
                            _config.MicrosoftClientId is { Length: > 0 };
        if (msConfigured)
        {
            if (_msAccount is null)
                throw new InvalidOperationException(
                    "微软登录状态不可用：请重新登录，或先在首页「退出登录」再用离线账号启动。");
            var account = _msAccount;
            if (account.McTokenExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(5))
            {
                Ui(() =>
                {
                    LaunchBar.IsIndeterminate = true;
                    LaunchStatus.Text = "正在续期微软登录…";
                });
                account = await new QlMicrosoftAuth()
                    .RefreshAsync(_config.MicrosoftClientId!, _config.MicrosoftRefreshToken!, ct);
                _msAccount = account;
                PersistMicrosoft(account);
                Ui(ApplyAccountUi);
            }
            return new QlProfile(account.Name, account.Uuid, account.McToken, "msa");
        }

        var name = (PlayerNameBox.Text ?? "").Trim();
        if (name.Length == 0)
            throw new InvalidOperationException("离线模式需要先在首页填写玩家名。");
        if (name.Length > 16) name = name[..16];
        _config.PlayerName = name;
        SaveConfigQuiet();
        Ui(() => PlayerNameBox.Text = name);
        return QlSession.Offline(name);
    }

    // ─────────────────────────── 游戏进程 ───────────────────────────

    private void OnGameLine(string line)
    {
        foreach (var plain in _logXml.Feed(line)) AppendLog(plain);
    }

    private void OnGameExit(int code)
    {
        var stderr = _game?.StderrTail ?? Array.Empty<string>();
        Ui(() =>
        {
            foreach (var plain in _logXml.FlushPending()) AppendLog(plain);
            AppendLog("[启动器] 游戏已退出，代码 " + code + "。");
            LaunchStatus.Text = code == 0 ? "游戏已正常退出。" : "游戏已退出（代码 " + code + "）。";
            LaunchHint.Text = "可以再次启动；日志保留供排查。";
            if (code != 0)
            {
                var context = new QlErrorContext(code, stderr, _recentLog.ToArray(),
                    QlLogTail.ReadTail(App.LogFile, 200));
                ShowDiagnosis(QlErrorDiagnoser.Diagnose(context));
                LaunchHint.Text = "已生成本地排查建议；可展开原始日志核对。";
            }
            else
            {
                ShowDiagnosis(null);
            }
            var old = _game;
            _game = null;
            old?.Dispose();
            LaunchBar.Visibility = Visibility.Collapsed;
            UpdateControls();
        });
    }

    private void OnKillGame(object sender, RoutedEventArgs e)
    {
        if (_game is not { } game) return;
        LaunchStatus.Text = "正在结束游戏进程…";
        game.Kill();
    }

    private void OnCancelPrep(object sender, RoutedEventArgs e)
    {
        _prepCts?.Cancel();
        LaunchStatus.Text = "正在取消…";
    }

    // ─────────────────────────── 杂项 ───────────────────────────

    private void OnMemoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingUi || MemoryBox.SelectedIndex < 0 ||
            MemoryBox.SelectedIndex >= _memoryValues.Count) return;
        _config.MaxMemoryMb = _memoryValues[MemoryBox.SelectedIndex];
        SaveConfigQuiet();
    }

    /// <summary>首页快捷档位；设置页写入非档位值时追加“自定义：N MB”。</summary>
    private void RebuildMemoryChoices(int autoMb)
    {
        _memoryValues.Clear();
        _memoryValues.Add(0);
        _memoryValues.AddRange(new[] { 4096, 6144, 8192, 12288, 16384 });
        bool custom = _config.MaxMemoryMb > 0 && !_memoryValues.Contains(_config.MaxMemoryMb);
        if (custom) _memoryValues.Add(_config.MaxMemoryMb);

        MemoryBox.Items.Clear();
        foreach (int value in _memoryValues)
        {
            string label = value == 0
                ? "自动（推荐：" + autoMb + " MB）"
                : custom && value == _config.MaxMemoryMb
                    ? "自定义：" + value + " MB"
                    : value + " MB";
            MemoryBox.Items.Add(label);
        }
        int index = _memoryValues.IndexOf(_config.MaxMemoryMb);
        MemoryBox.SelectedIndex = index >= 0 ? index : 0;
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        LaunchLog.Clear();
        _recentLog.Clear();
        ShowDiagnosis(null);
    }
    private void OnOpenGameDir(object sender, RoutedEventArgs e)
    {
        var dir = _runDir ?? _gameRoot;
        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) Globals.OpenUrl(dir);
    }

    private void OnOpenLogDir(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(App.DataDir);
        Globals.OpenUrl(App.DataDir);
    }

    private void OnGoSync(object sender, RoutedEventArgs e) => MainWindow.Instance.Navigate(MainWindow.PageKey.Sync);

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateControls();
        MainWindow.Instance.SetStatus(busy ? "启动准备中…" : "准备就绪");
    }

    private void UpdateControls()
    {
        bool running = _game is { HasExited: false };
        bool ready = _runDir is not null && _version is not null;
        LaunchButton.IsEnabled = !_busy && !running && ready;
        LaunchButton.Content = _busy ? "准备中…" : running ? "游戏运行中" : "启动游戏";
        BtnKillGame.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        BtnCancelPrep.Visibility = _busy && _prepCts is not null ? Visibility.Visible : Visibility.Collapsed;
        BtnGoSync.Visibility = !ready && !_busy ? Visibility.Visible : Visibility.Collapsed;
        PlayerNameBox.IsEnabled = !_busy && _msAccount is null;
        BtnMsLogin.IsEnabled = !_busy && _msAccount is null &&
                               !string.IsNullOrWhiteSpace(_config.MicrosoftClientId);
        BtnLogout.IsEnabled = !_busy;
        MemoryBox.IsEnabled = !_busy && !running;
    }

    private void SetProgress(int done, int total, string prefix)
    {
        LaunchBar.Visibility = Visibility.Visible;
        LaunchBar.IsIndeterminate = false;
        LaunchBar.Maximum = Math.Max(total, 1);
        LaunchBar.Value = Math.Min(done, total);
        LaunchStatus.Text = total > 0 ? prefix + " " + done + "/" + total : prefix;
    }

    private void AppendLog(string line)
    {
        Ui(() =>
        {
            _recentLog.Enqueue(line);
            while (_recentLog.Count > 300) _recentLog.Dequeue();
            if (LaunchLog.Text == LogPlaceholder) LaunchLog.Clear();
            LaunchLog.AppendText(line + Environment.NewLine);
            if (LaunchLog.Text.Length > 200_000)
                LaunchLog.Text = LaunchLog.Text[^100_000..];
            LaunchLog.ScrollToEnd();
        });
    }

    /// <summary>把本地归因卡接入现有启动卡片；同一日志指纹不重复插入。</summary>
    private void ShowDiagnosis(QlDiagnosis? diagnosis)
    {
        if (DiagnosisHost is null) return;
        if (diagnosis is null)
        {
            DiagnosisHost.Children.Clear();
            _lastDiagnosisFingerprint = null;
            return;
        }
        if (diagnosis.Fingerprint == _lastDiagnosisFingerprint &&
            DiagnosisHost.Children.Count > 0)
            return;
        _lastDiagnosisFingerprint = diagnosis.Fingerprint;
        DiagnosisHost.Children.Clear();
        DiagnosisHost.Children.Add(ConfigNotice.Build(diagnosis));
    }

    private void SaveConfigQuiet()
    {
        try
        {
            _config.Save(App.ConfigDir);
        }
        catch (Exception ex)
        {
            Globals.Log("配置保存失败: " + ex.Message);
            AppendLog("[启动器] 配置保存失败：" + ex.Message);
        }
    }

    private void Ui(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else Dispatcher.BeginInvoke(action);
    }

    private static string FriendlyMessage(Exception e) => e switch
    {
        QlAuthException auth => auth.Message,
        IOException io => io.Message,
        UnauthorizedAccessException => "文件被占用或无权限：" + e.Message,
        _ => e.Message,
    };

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
}
