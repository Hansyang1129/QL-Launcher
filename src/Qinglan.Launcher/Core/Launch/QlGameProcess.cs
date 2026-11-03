using System.Diagnostics;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>
/// 游戏进程壳：启 java、逐行转发 stdout+stderr、报退出码。日志面板（M2b UI）接在事件上。
/// </summary>
public sealed class QlGameProcess : IDisposable
{
    private readonly Process _proc;
    private readonly Action<string>? _onLine;
    private readonly Action<int>? _onExit;
    private int _exitCode = int.MinValue;

    /// <param name="onLine">每行回调（stderr 行前缀 "(ERR) "）；可能并发到达，消费方自行排队。</param>
    /// <param name="onExit">退出码回调（崩溃时也会来一发）。</param>
    public QlGameProcess(string javaExe, IReadOnlyList<string> args, string workDir,
        Action<string>? onLine = null, Action<int>? onExit = null)
    {
        _onLine = onLine;
        _onExit = onExit;
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null) _onLine?.Invoke(e.Data);
        };
        _proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null) _onLine?.Invoke("(ERR) " + e.Data);
        };
        _proc.Exited += (_, _) =>
        {
            _exitCode = SafeExitCode();
            _onExit?.Invoke(_exitCode);
        };
        _proc.Start();
        _proc.BeginOutputReadLine();
        _proc.BeginErrorReadLine();
        Globals.Log("游戏进程已启动 pid=" + _proc.Id);
    }

    /// <summary>启动异常/崩溃在这里吐出；进程未退出时给 int.MinValue。</summary>
    public int CurrentExitCode => Volatile.Read(ref _exitCode);
    public bool HasExited => _proc.HasExited;

    /// <summary>终止整棵进程树（关 MC 等同于按电源键）。返回是否真得手。</summary>
    public bool Kill()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception e)
        {
            Globals.Log("进程结束失败: " + e.Message);
            return false;
        }
    }

    private int SafeExitCode()
    {
        try { return _proc.ExitCode; }
        catch { return -999; }
    }

    public void Dispose()
    {
        _proc.Dispose();
    }
}
