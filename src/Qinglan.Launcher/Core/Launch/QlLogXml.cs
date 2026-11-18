using System.Text;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>
/// Minecraft 控制台的 <c>LegacyXMLLayout</c> 事件还原器：把跨行的
/// <c>&lt;log4j:Event logger=… level=… thread=…&gt;…&lt;/log4j:Event&gt;</c>
/// 收敛成 latest.log 同款 <c>[thread/LEVEL]: message</c> 单行文本；非 XML 行原样透传。
/// <para>只做展示层整形，不改游戏日志配置、不落盘。解析失败/超长事件回退原文，保证不丢信息。</para>
/// </summary>
public sealed class QlLogXml
{
    private const int MaxEventLines = 400;

    private StringBuilder? _buf;
    private int _lines;

    public string[] Feed(string? line)
    {
        line ??= "";
        if (_buf is null)
        {
            if (!line.Contains("<log4j:Event", StringComparison.Ordinal))
                return new[] { line };
            _buf = new StringBuilder();
            _buf.AppendLine(line);
            _lines = 1;
            if (line.Contains("</log4j:Event>", StringComparison.Ordinal)) return Flush();
            return Array.Empty<string>();
        }

        _buf.AppendLine(line);
        _lines++;
        if (line.Contains("</log4j:Event>", StringComparison.Ordinal) || _lines >= MaxEventLines)
            return Flush();
        return Array.Empty<string>();
    }

    /// <summary>进程退出时把没等到收尾标签的事件吐出来（原文兜底）。</summary>
    public string[] FlushPending()
    {
        if (_buf is null) return Array.Empty<string>();
        return Flush();
    }

    private string[] Flush()
    {
        var raw = _buf!.ToString();
        _buf = null;
        var parsed = Parse(raw);
        return parsed is null
            ? raw.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : new[] { parsed };
    }

    /// <summary>纯函数：一个完整事件 → 单行文本；缺关键字段时返回 null（调用方回退原文）。</summary>
    public static string? Parse(string raw)
    {
        int open = raw.IndexOf("<log4j:Event", StringComparison.Ordinal);
        int openEnd = open < 0 ? -1 : raw.IndexOf('>', open);
        if (open < 0 || openEnd < 0) return null;

        var tag = raw[open..(openEnd + 1)];
        var level = Attr(tag, "level") ?? "INFO";
        var thread = Attr(tag, "thread");
        var message = CData(raw, "<log4j:Message><![CDATA[", "]]></log4j:Message>");
        if (message is null) return null;

        var sb = new StringBuilder();
        sb.Append('[').Append(string.IsNullOrEmpty(thread) ? "?" : thread).Append('/')
          .Append(level).Append("]: ").Append(message.Replace("\r", "").TrimEnd('\n'));

        var throwable = CData(raw, "<log4j:Throwable><![CDATA[", "]]></log4j:Throwable>");
        if (!string.IsNullOrWhiteSpace(throwable))
            sb.Append('\n').Append(throwable.Replace("\r", "").TrimEnd('\n'));
        return sb.ToString();
    }

    private static string? Attr(string tag, string name)
    {
        var needle = name + "=\"";
        int i = tag.IndexOf(needle, StringComparison.Ordinal);
        if (i < 0) return null;
        i += needle.Length;
        int end = tag.IndexOf('"', i);
        return end < 0 ? null : tag[i..end];
    }

    private static string? CData(string raw, string open, string close)
    {
        int i = raw.IndexOf(open, StringComparison.Ordinal);
        if (i < 0) return null;
        i += open.Length;
        int end = raw.IndexOf(close, i, StringComparison.Ordinal);
        return end < 0 ? null : raw[i..end];
    }
}
