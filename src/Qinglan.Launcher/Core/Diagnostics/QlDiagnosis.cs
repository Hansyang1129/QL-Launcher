namespace Qinglan.Launcher.Core.Diagnostics;

/// <summary>一次本地归因的输入：进程退出码 + 三类日志 + 启动前异常。</summary>
public sealed record QlErrorContext(
    int ExitCode,
    IReadOnlyList<string> Stderr,
    IReadOnlyList<string> RecentLog,
    IReadOnlyList<string> LauncherLog,
    Exception? StartException = null);

/// <summary>归因结果；只用于本地展示，不含任何外发地址。</summary>
public sealed record QlDiagnosis(
    string RuleId,
    string Category,
    string Title,
    string Conclusion,
    IReadOnlyList<string> Suggestions,
    string RawExcerpt,
    string Fingerprint,
    int SizeBytes);
