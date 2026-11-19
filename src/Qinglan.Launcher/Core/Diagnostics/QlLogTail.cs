using System.Text;

namespace Qinglan.Launcher.Core.Diagnostics;

/// <summary>只读 launcher.log 尾部；归因展示用，不上传、不产生 URL。</summary>
public static class QlLogTail
{
    public static IReadOnlyList<string> ReadTail(string path, int maxLines,
        int maxBytes = 64 * 1024)
    {
        if (maxLines <= 0) return Array.Empty<string>();
        try
        {
            if (!File.Exists(path)) return Array.Empty<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite);
            long start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = reader.ReadToEnd()
                .Replace("\r\n", "\n")
                .Split('\n')
                .Where(line => line.Length > 0)
                .ToList();
            if (start > 0 && lines.Count > 0) lines.RemoveAt(0); // 丢弃被截断的首行
            return lines.TakeLast(maxLines).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
