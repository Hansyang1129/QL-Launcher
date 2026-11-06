using System.Text.RegularExpressions;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core.Launch;

/// <summary>
/// Mojang 官方 JRE 运行时下载（HMCL MojangJavaDownloadTask 同源语义）：
/// all.json（固定 sha 地址） → downloads[平台][component] 候选里取首个大版本达标的 →
/// 拉 manifest（files 表） → 缺才下（SHA-1 强校验） → 完成后在 installDir 里扫 bin/java.exe。
/// v1 只吃 raw 下载（压缩体 LZMA 条目跳过报错；JRE 总盘 ~40MB，直接拉可接受）。
/// 产物落在 dataDir/runtimes/&lt;component&gt;/（QlJavaLocator 已扫 runtimes/ 位）。
/// </summary>
public static partial class QlMojangRuntime
{
    public const string JavaListUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    /// <summary>Mojang 运行时平台标（1.20.1 的 java-runtime-gamma 在 windows-x64 位）。</summary>
    public static string PlatformName()
        => QlLaunchPrep.ArchLabel() switch
        {
            "arm64" => "windows-arm64",
            "x86" => "windows-x86",
            _ => "windows-x64",
        };

    /// <summary>候选 version.name 的大版本抽取（"17.0.8_7"/"openjdk-17.0.8"→17；首个整数段）。</summary>
    public static int ParseNameVersion(string name)
    {
        var m = DigitRun().Match(name ?? "");
        return m.Success && int.TryParse(m.Value, out var v) ? v : -1;
    }

    /// <summary>解析运行时清单路由：candidate 首个 version>=minMajor。</summary>
    public static async Task<(string VersionName, string ManifestUrl)> ResolveAsync(
        string component, int minMajor, string? listUrl = null, CancellationToken ct = default)
    {
        var text = await QlHttp.GetTextAsync(listUrl ?? JavaListUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("downloads", out var downloads))
            throw new IOException("运行时清单缺 downloads");
        if (!downloads.TryGetProperty(PlatformName(), out var byPlatform))
            throw new IOException("运行时清单没有平台 " + PlatformName());
        if (!byPlatform.TryGetProperty(component, out var candidates))
            throw new IOException("运行时清单没有组件 " + component);
        foreach (var c in candidates.EnumerateArray())
        {
            var name = c.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var n)
                ? n.GetString() ?? ""
                : "";
            if (ParseNameVersion(name) >= minMajor)
            {
                var url = c.TryGetProperty("manifest", out var mf) ? mf.TryGetProperty("url", out var mu) ? mu.GetString() : null : null;
                if (!string.IsNullOrEmpty(url))
                    return (name, url!);
            }
        }
        throw new IOException("没有大版本 >= " + minMajor + " 的 " + component + " 候选");
    }

    /// <summary>
    /// 装配：manifest files 表逐条规划（就位/校验不符重下、目录创建、仅 LZMA 报错），
    /// 8 路下载后扫 bin/java.exe。返回 java.exe 所在的 Home 目录。
    /// </summary>
    public static async Task<string> InstallAsync(string component, string manifestUrl, string installDir,
        Action<int, int>? progress = null, CancellationToken ct = default)
    {
        var text = await QlHttp.GetTextAsync(manifestUrl, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        if (!doc.RootElement.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object)
            throw new IOException("JRE manifest 缺 files 表");

        var planned = new List<QlFileTask>();
        var skipped = 0;
        foreach (var f in filesEl.EnumerateObject())
        {
            var kind = f.Value.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (kind == "directory")
            {
                Directory.CreateDirectory(Path.Combine(installDir, f.Name));
                continue;
            }
            if (kind == "link")
                continue; // Windows 装配面无软链需求
            if (kind != "file")
                continue;

            var target = Path.Combine(installDir, f.Name);
            var raw = f.Value.TryGetProperty("downloads", out var dls) &&
                      dls.TryGetProperty("raw", out var r) ? r : (JsonElement?)null;
            if (raw is null)
            {
                // 只有 lzma 的条目 v1 不解压（需手写 LZMA）；记数并在末尾汇总报错
                var lz = f.Value.TryGetProperty("downloads", out var d2) && d2.TryGetProperty("lzma", out var lz2);
                if (lz) throw new IOException("JRE 文件仅提供 LZMA 压缩体，需扩展解压能力: " + f.Name);
                continue;
            }
            var sha1 = raw.Value.TryGetProperty("sha1", out var sh) ? sh.GetString() ?? "" : "";
            var url = raw.Value.TryGetProperty("url", out var ru) ? ru.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(sha1) || string.IsNullOrEmpty(url))
                throw new IOException("JRE 文件条目缺 url/sha1: " + f.Name);

            if (File.Exists(target) && QlHttp.Sha1Hex(target) == sha1) { skipped++; continue; }
            planned.Add(new QlFileTask("JRE " + f.Name, new[] { url }, target, sha1,
                raw.Value.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : -1));
        }

        await QlLaunchPrep.DownloadAllAsync(planned, progress, ct).ConfigureAwait(false);

        // 定位 bin/java.exe（manifest 键带上组件/平台前缀的写法也兼容）
        var exe = Directory.EnumerateFiles(installDir, "java.exe", SearchOption.AllDirectories)
            .Where(p => string.Equals(Path.GetFileName(Path.GetDirectoryName(p)), "bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Length)
            .FirstOrDefault()
            ?? throw new IOException("JRE 安装目录里找不到 bin/java.exe: " + installDir);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(exe)!, ".."));
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex DigitRun();
}
