using System.Text;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>用户额外 JVM 参数解析后的最终计划。</summary>
/// <param name="UserProvidedXmx">用户是否自己写了 <c>-Xmx</c>。</param>
/// <param name="XmxToken">最终唯一的 <c>-Xmx</c> token（用户值或内存字段生成）。</param>
/// <param name="XmxMb">最终 <c>-Xmx</c> 换算出的 MB，用于软提示。</param>
/// <param name="UserProvidedLog4j">用户是否覆盖了 log4j 配置路径。</param>
/// <param name="UserTokens">用户参数（已去掉 <c>-Xmx</c>，保留 log4j 覆盖项）。</param>
public sealed record QlJvmPlan(bool UserProvidedXmx, string XmxToken, int XmxMb,
    bool UserProvidedLog4j, IReadOnlyList<string> UserTokens);

/// <summary>
/// 额外 JVM 参数的自由输入层：分词、引号、<c>-Xmx</c> 去重、log4j 覆盖、校验与预览。
/// <para>只做字符串处理，不启动进程、不发网络请求；默认空输入时输出与 R1 等价。</para>
/// </summary>
public static class QlJvmOptions
{
    public const int MaxTextLength = 4096;
    public const int MaxMemoryMb = 1048576;

    /// <summary>内存输入：空 = 自动（0）；只接受非负整数 MB。</summary>
    public static bool TryParseMemory(string? text, out int mb, out string? error)
    {
        mb = 0;
        error = null;
        var s = (text ?? "").Trim();
        if (s.Length == 0) return true;
        if (!s.All(char.IsDigit))
        {
            error = "内存上限只能填数字（MB）；留空表示自动。";
            return false;
        }
        if (!int.TryParse(s, out mb) || mb < 0)
        {
            error = "内存值超出范围。";
            return false;
        }
        if (mb > MaxMemoryMb)
        {
            error = "内存上限过大（最大 " + MaxMemoryMb + " MB）。";
            return false;
        }
        return true;
    }

    /// <summary>解析额外 JVM 参数。失败时 <paramref name="error"/> 给行内提示文案。</summary>
    public static bool TryParse(string? raw, int defaultMemoryMb, out QlJvmPlan plan,
        out string? error)
    {
        if (defaultMemoryMb <= 0) defaultMemoryMb = 2048;
        if (defaultMemoryMb > MaxMemoryMb) defaultMemoryMb = MaxMemoryMb;
        plan = new QlJvmPlan(false, "-Xmx" + defaultMemoryMb + "M", defaultMemoryMb, false,
            Array.Empty<string>());

        if (!TryTokenize(raw, out var tokens, out error)) return false;

        bool hasXmx = false, hasLog4j = false;
        string? xmxToken = null;
        int xmxMb = 0;
        var rest = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];

            if (token.StartsWith("-Dlog4j.configurationFile", StringComparison.Ordinal))
            {
                if (!token.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal))
                {
                    error = "log4j 配置要写成 -Dlog4j.configurationFile=<路径>。";
                    return false;
                }
                if (hasLog4j)
                {
                    error = "只保留一个 -Dlog4j.configurationFile。";
                    return false;
                }
                hasLog4j = true;
                rest.Add(token);
                continue;
            }

            if (token == "-Xmx")
            {
                if (i + 1 >= tokens.Count)
                {
                    error = "-Xmx 后面缺少数值。";
                    return false;
                }
                if (hasXmx)
                {
                    error = "只保留一个 -Xmx；请删除重复项。";
                    return false;
                }
                if (!TryParseXmx(tokens[++i], out xmxMb, out string canonical, out error))
                    return false;
                hasXmx = true;
                xmxToken = "-Xmx" + canonical;
                continue;
            }

            if (token.StartsWith("-Xmx", StringComparison.Ordinal) && token.Length > 4)
            {
                if (hasXmx)
                {
                    error = "只保留一个 -Xmx；请删除重复项。";
                    return false;
                }
                if (!TryParseXmx(token[4..], out xmxMb, out string canonical, out error))
                    return false;
                hasXmx = true;
                xmxToken = "-Xmx" + canonical;
                continue;
            }

            rest.Add(token);
        }

        if (!hasXmx)
        {
            xmxToken = "-Xmx" + defaultMemoryMb + "M";
            xmxMb = defaultMemoryMb;
        }
        plan = new QlJvmPlan(hasXmx, xmxToken!, xmxMb, hasLog4j, rest);
        return true;
    }

    /// <summary>按双引号切分参数；支持 <c>\"</c> 转义，拒绝换行/控制字符/注入字符。</summary>
    public static bool TryTokenize(string? raw, out IReadOnlyList<string> tokens, out string? error)
    {
        var list = new List<string>();
        tokens = list;
        error = null;
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (raw.Length > MaxTextLength)
        {
            error = "额外 JVM 参数过长（上限 " + MaxTextLength + " 字符）。";
            return false;
        }

        var sb = new StringBuilder();
        bool inQuote = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c is '\r' or '\n')
            {
                error = "额外 JVM 参数不能包含换行；需要分行显示请勾选「每个参数一行」。";
                return false;
            }
            if (c is '&' or '|' or '<' or '>' or '^')
            {
                error = "额外 JVM 参数不能包含 & | < > ^。";
                return false;
            }
            if (c == '"')
            {
                inQuote = !inQuote;
                continue;
            }
            if (c == '\\' && inQuote && i + 1 < raw.Length && raw[i + 1] == '"')
            {
                sb.Append('"');
                i++;
                continue;
            }
            if (!inQuote && (c == ' ' || c == '\t'))
            {
                Flush();
                continue;
            }
            if (char.IsControl(c))
            {
                error = "额外 JVM 参数不能包含控制字符。";
                return false;
            }
            sb.Append(c);
        }
        if (inQuote)
        {
            error = "引号未闭合。";
            return false;
        }
        Flush();
        return true;

        void Flush()
        {
            if (sb.Length == 0) return;
            list.Add(sb.ToString());
            sb.Clear();
        }
    }

    /// <summary>把原始参数按顶层空格换成换行（引号内空格保留），用于“每个参数一行”展示。</summary>
    public static string FormatPretty(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var sb = new StringBuilder(raw.Length + 8);
        bool inQuote = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = raw[i];
            if (c == '\\' && inQuote && i + 1 < raw.Length && raw[i + 1] == '"')
            {
                sb.Append(c).Append(raw[++i]);
                continue;
            }
            if (c == '"')
            {
                inQuote = !inQuote;
                sb.Append(c);
                continue;
            }
            if (!inQuote && (c is ' ' or '\t' or '\r' or '\n'))
            {
                if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim('\n');
    }

    /// <summary>把“每个参数一行”的展示文本还原为空格分隔的原始参数。</summary>
    public static string FormatRaw(string? pretty)
    {
        if (string.IsNullOrEmpty(pretty)) return "";
        var sb = new StringBuilder(pretty.Length);
        bool inQuote = false;
        for (int i = 0; i < pretty.Length; i++)
        {
            char c = pretty[i];
            if (c == '\\' && inQuote && i + 1 < pretty.Length && pretty[i + 1] == '"')
            {
                sb.Append(c).Append(pretty[++i]);
                continue;
            }
            if (c == '"')
            {
                inQuote = !inQuote;
                sb.Append(c);
                continue;
            }
            if (c is '\r' or '\n')
            {
                if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    /// <summary>设置页只读预览：JVM 段（不含游戏参数/token）。</summary>
    public static IReadOnlyList<string> BuildPreview(QlJvmPlan plan,
        bool useVersionJvmArgs = true)
    {
        var lines = new List<string> { plan.XmxToken };
        lines.Add(useVersionJvmArgs
            ? "[版本 JSON 自带 JVM 参数：启动时插在此处]"
            : "[已关闭版本 JSON 自带 JVM 参数]");
        foreach (string token in plan.UserTokens) lines.Add(QuoteIfNeeded(token));
        if (!plan.UserProvidedLog4j) lines.Add("-Dlog4j.configurationFile=<启动器注入>");
        lines.Add("<主类>");
        return lines;
    }

    /// <summary>把 token 拼回可编辑的原始参数文本（含空格的 token 自动加引号）。</summary>
    public static string JoinTokens(IEnumerable<string> tokens)
        => string.Join(" ", tokens.Select(QuoteIfNeeded));

    private static bool TryParseXmx(string value, out int mb, out string canonical,
        out string? error)
    {
        mb = 0;
        canonical = "";
        error = null;
        string s = (value ?? "").Trim();
        if (s.Length == 0)
        {
            error = "-Xmx 的值不能为空。";
            return false;
        }

        char unit = s[^1];
        string numberPart = char.IsLetter(unit) ? s[..^1] : s;
        if (numberPart.Length == 0 || !numberPart.All(char.IsDigit) ||
            !long.TryParse(numberPart, out long number) || number <= 0)
        {
            error = "-Xmx 的值必须是正整数，可带 K/M/G 单位。";
            return false;
        }

        long mbValue;
        string suffix;
        switch (char.ToLowerInvariant(unit))
        {
            case 'g':
                if (number > MaxMemoryMb / 1024) { error = "-Xmx 过大。"; return false; }
                mbValue = number * 1024;
                suffix = "G";
                break;
            case 'm':
                if (number > MaxMemoryMb) { error = "-Xmx 过大。"; return false; }
                mbValue = number;
                suffix = "M";
                break;
            case 'k':
                if (number > MaxMemoryMb * 1024L) { error = "-Xmx 过大。"; return false; }
                mbValue = number * 1024 / (1024 * 1024);
                suffix = "K";
                break;
            default:
                // 不写单位时按 MB 处理，并补 M，避免 Java 把它当字节数。
                if (number > MaxMemoryMb) { error = "-Xmx 过大。"; return false; }
                mbValue = number;
                suffix = "M";
                break;
        }

        if (mbValue < 1)
        {
            error = "-Xmx 的值太小（至少 1 MB）。";
            return false;
        }
        mb = (int)mbValue;
        canonical = number + suffix;
        return true;
    }

    private static string QuoteIfNeeded(string token)
        => token.Any(char.IsWhiteSpace) || token.Contains('"')
            ? "\"" + token.Replace("\"", "\\\"") + "\""
            : token;
}
