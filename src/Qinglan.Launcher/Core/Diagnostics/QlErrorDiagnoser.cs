using System.Text;

namespace Qinglan.Launcher.Core.Diagnostics;

/// <summary>纯本地归因器：stderr / launcher.log / log4j Throwable / 退出码 → 人话结论。</summary>
public static class QlErrorDiagnoser
{
    public static QlDiagnosis Diagnose(QlErrorContext context)
    {
        string combined = BuildCombined(context);
        foreach (var rule in QlErrorRules.All.OrderBy(r => r.Priority))
        {
            bool patternHit = rule.Patterns.Any(p =>
                combined.Contains(p, StringComparison.OrdinalIgnoreCase));
            bool codeHit = rule.ExitCodes.Length > 0 &&
                           rule.ExitCodes.Contains(context.ExitCode);
            if (patternHit || codeHit) return Build(rule, context);
        }
        return Build(QlErrorRules.Unknown, context);
    }

    private static string BuildCombined(QlErrorContext context)
    {
        var lines = new List<string>();
        lines.AddRange(context.Stderr);
        lines.AddRange(context.RecentLog);
        lines.AddRange(context.LauncherLog);
        if (context.StartException is not null)
            lines.Add(context.StartException.ToString());
        return string.Join("\n", lines);
    }

    private static QlDiagnosis Build(QlErrorRule rule, QlErrorContext context)
    {
        var lines = new List<string> { "exit code: " + context.ExitCode };
        lines.AddRange(context.Stderr);
        lines.AddRange(context.RecentLog);
        lines.AddRange(context.LauncherLog);
        if (context.StartException is not null)
            lines.Add(context.StartException.ToString());

        string excerpt = string.Join("\n", lines.TakeLast(160));
        if (string.IsNullOrWhiteSpace(excerpt))
            excerpt = "exit code: " + context.ExitCode + "（没有捕获到日志）";

        byte[] bytes = Encoding.UTF8.GetBytes(excerpt);
        string fingerprint = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        return new QlDiagnosis(rule.Id, rule.Category, rule.Title, rule.Conclusion,
            rule.Suggestions, excerpt, fingerprint, bytes.Length);
    }
}
