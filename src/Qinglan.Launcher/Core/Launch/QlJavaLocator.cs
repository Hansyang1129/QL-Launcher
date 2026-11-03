using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>探测到的一份 Java：exe 路径 + 版本串 + 大版本。</summary>
/// <param name="ExePath">java.exe 绝对路径。</param>
/// <param name="Version">原始版本串（如 17.0.9）。</param>
/// <param name="MajorVersion">大版本（1.8 记法折算成 8）。</param>
public sealed record QlJavaRuntime(string ExePath, string Version, int MajorVersion)
{
    public string HomeDir => Path.GetFullPath(Path.Combine(ExePath, "..", ".."));
}

/// <summary>游戏所需 Java（1.20.1 → 17+）的机器探测：扫描常用安装位，-version 判真伪。</summary>
public static partial class QlJavaLocator
{
    public const int DefaultMinMajor = 17;

    public static List<QlJavaRuntime> Scan(string? preferredDir)
    {
        var exes = new List<string>();
        void AddDir(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            try
            {
                dir = Environment.ExpandEnvironmentVariables(dir);
                var exe = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(exe)) exes.Add(exe);
            }
            catch
            {
                // 扩不开/无权限的目录不算数
            }
        }

        AddDir(preferredDir); // 设置页手工指定优先
        AddDir(Environment.GetEnvironmentVariable("JAVA_HOME"));

        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)") })
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Zulu", "Microsoft", "Semeru", "BellSoft", "Amazon Corretto" })
                if (!string.IsNullOrWhiteSpace(root))
                    foreach (var sub in SafeDirs(root, vendor))
                        AddDir(sub);

        foreach (var sub in SafeDirs(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"))
            AddDir(sub);

        // PATH 里每个目录
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries)) AddDir(dir);

        // 下载位 JRE（M2b 落地 QLRuntime 下载后挂这里）
        foreach (var sub in SafeDirs(Path.Combine(App.DataDir), "runtimes"))
            AddDir(sub);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<QlJavaRuntime>();
        foreach (var exe in exes)
            if (seen.Add(exe))
            {
                var jit = TryParse(exe);
                if (jit is not null) found.Add(jit);
            }
        return found.OrderByDescending(j => j.MajorVersion).ThenByDescending(j => j.Version).ToList();
    }

    /// <summary>没指定就取探测表第一个达标者（>= minMajor，而后再高不嫌）。</summary>
    public static QlJavaRuntime? FindBest(string? preferredDir, int minMajor = DefaultMinMajor)
    {
        var list = Scan(preferredDir);
        return list.FirstOrDefault(j => j.MajorVersion >= minMajor);
    }

    /// <summary>跑 <c>java -version</c> 验 exe 真伪并读版本；失败/超时 → null。</summary>
    public static QlJavaRuntime? TryParse(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "-version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            if (!p.WaitForExit(10_000))
            {
                try { p.Kill(true); } catch (Exception) { /* 收尸失败就算了 */ }
                return null;
            }
            var output = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            var (raw, major) = ParseVersionOutput(output);
            if (raw is null) return null;
            return new QlJavaRuntime(javaExe, raw, major);
        }
        catch (Exception e)
        {
            Globals.Log("Java 探测失败 " + javaExe + ": " + e.Message);
            return null;
        }
    }

    /// <summary>版本行解析（纯函数，便于单测）：报 <c>(原始串, 大版本)</c>，认不出 = (null, -1)。</summary>
    public static (string? Raw, int Major) ParseVersionOutput(string output)
    {
        var m = VersionLine().Match(output);
        if (!m.Success) return (null, -1);
        var raw = m.Groups[1].Value;
        var head = raw.Split('_')[0].Split('-')[0].Split('+')[0];
        var parts = head.Split('.');
        int major;
        if (parts.Length >= 2 && parts[0] == "1")
            // 1.8.0_302 → 8
            major = int.TryParse(parts[1], out var legacy) ? legacy : -1;
        else
            major = int.TryParse(parts[0], out var modern) ? modern : -1;
        return (raw, major);
    }

    private static IEnumerable<string> SafeDirs(string root, string sub)
    {
        try
        {
            var dir = Path.Combine(root, sub);
            return Directory.Exists(dir)
                ? Directory.EnumerateDirectories(dir)
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    [GeneratedRegex("version \"([^\"]+)\"")]
    private static partial Regex VersionLine();
}
