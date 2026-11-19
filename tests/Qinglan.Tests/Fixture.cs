using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Qinglan.Tests;

/// <summary>测试夹具：临时目录、manifest 构造、mrpack 打包。</summary>
internal static class Fx
{
    private static readonly List<string> TempDirs = new();

    /// <summary>临时根目录：优先 QL_TEST_TMP；否则放测试输出目录（bin，已 ignore）；最后回落系统 Temp。</summary>
    private static string TempRoot()
    {
        var custom = Environment.GetEnvironmentVariable("QL_TEST_TMP");
        foreach (var dir in new[]
                 {
                     string.IsNullOrWhiteSpace(custom) ? null : custom,
                     Path.Combine(AppContext.BaseDirectory, "test-tmp"),
                     Path.Combine(Path.GetTempPath(), "qinglan-launcher-tests"),
                     Path.Combine(Path.GetTempPath(), "opencode", "qinglan-launcher-tests"),
                 })
        {
            if (dir is null) continue;
            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // 换下一个候选
            }
        }
        throw new IOException("无法创建测试临时目录");
    }

    /// <summary>起一个进程级防重的临时目录（结束统一清）。TestOut 规约：放系统临时目录随机名。</summary>
    public static string TempDir()
    {
        var dir = Path.Combine(TempRoot(), "ql-tests-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        TempDirs.Add(dir);
        return dir;
    }

    public static void CleanupAll()
    {
        lock (TempDirs)
        {
            foreach (var d in TempDirs)
            {
                try { Directory.Delete(d, recursive: true); }
                catch (IOException) { }
            }
            TempDirs.Clear();
        }
    }

    public static string Sha1(byte[] data) => Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
    public static string Sha1(string text) => Sha1(Encoding.UTF8.GetBytes(text));
    public static int SizeOf(string text) => Encoding.UTF8.GetByteCount(text);

    public sealed class ManifestData
    {
        public required string Json;
        public required Dictionary<string, string> Contents;
    }

    /// <summary>构造 manifest：内容字典自带 sha1/size/urls（占位后统一替换 base url）。</summary>
    public static ManifestData Manifest(
        (string Path, string Content, string? Group)[] std,
        (string Path, string Content, string? Group)[]? pfm = null,
        (string Path, string Content)[]? common = null,
        string version = "1.0.0-test")
    {
        var contents = new Dictionary<string, string>(StringComparer.Ordinal);

        string Entry(string path, string content, string? group)
        {
            contents[path] = content;
            var opt = group == null ? "" : ", \"optional\": \"" + group + "\"";
            return
                "{ \"path\": \"" + Esc(path) + "\", \"sha1\": \"" + Sha1(content) + "\", \"size\": " +
                SizeOf(content) + ", \"urls\": [\"URLPLACEHOLDER" + Esc(path) + "\"]" + opt + " }";
        }

        var pfmList = pfm ?? std.Select(e => (e.Path, e.Content, e.Group)).ToArray();
        var commonList = common ?? Array.Empty<(string, string)>();

        var doc =
            "{ \"schema\": 1, \"pack\": { \"name\": \"Qinglan-Test\", \"version\": \"" + version +
            "\", \"updatedAt\": \"2026-11-02T00:00:00Z\" }, \"channels\": { \"std\": { \"label\": \"官方包\", " +
            "\"mods\": [" + string.Join(",", std.Select(e => Entry(e.Path, e.Content, e.Group))) +
            "], \"files\": [] }, \"pfm\": { \"label\": \"优化包\", \"mods\": [" +
            string.Join(",", pfmList.Select(e => Entry(e.Path, e.Content, e.Group))) + "], \"files\": [] }, " +
            "\"common\": { \"label\": \"公共文件\", \"mods\": [], \"files\": [" +
            string.Join(",", commonList.Select(c => Entry(c.Path, c.Content, null))) + "] } } }";
        return new ManifestData { Json = doc, Contents = contents };
    }

    /// <summary>URL 占位 → fixture base。</summary>
    public static string WithBaseUrl(string json, string baseUrl)
        => json.Replace("URLPLACEHOLDER", baseUrl);

    public static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>最小 mrpack：overrides + index 文件（modrinth.index.json 声明 CDN 文件）。</summary>
    public static string BuildMrpack(string outputZip, MiniHttpServer server,
        List<(string Path, string Content)> indexFiles, (string Path, string Content)[] overrideFiles,
        string mcVersion = "1.20.1", string loader = "0.16.9", string versionId = "Qinglan-Test 1.0-full")
    {
        return BuildMrpackRaw(outputZip, server,
            indexFiles.Select(f => (f.Path, (byte[])Encoding.UTF8.GetBytes(f.Content))).ToList(),
            overrideFiles.Select(f => (f.Path, (byte[])Encoding.UTF8.GetBytes(f.Content))).ToArray(),
            mcVersion, loader, versionId);
    }

    /// <summary>字节内容版 mrpack 构造（二进制 mod jar 等场景）；BuildMrpack 的底层。</summary>
    public static string BuildMrpackRaw(string outputZip, MiniHttpServer server,
        List<(string Path, byte[] Content)> indexFiles, (string Path, byte[] Content)[] overrideFiles,
        string mcVersion = "1.20.1", string loader = "0.16.9", string versionId = "Qinglan-Test 1.0-full")
    {
        using var zipStream = File.Create(outputZip);
        using var zip = new ZipArchive(zipStream, ZipArchiveMode.Create);

        foreach (var (path, content) in overrideFiles)
        {
            var e = zip.CreateEntry("overrides/" + path);
            using var w = e.Open();
            w.Write(content);
        }

        var filesJson = indexFiles.Select(f =>
            "{ \"path\": \"" + Esc(f.Path) + "\", \"hashes\": { \"sha1\": \"" + Sha1(f.Content) +
            "\" }, \"env\": null, \"downloads\": [\"" + server.BaseUrl + Esc(f.Path) +
            "\"], \"fileSize\": " + f.Content.Length + " }");
        var index =
            "{ \"formatVersion\": 1, \"game\": \"minecraft\", \"versionId\": \"" + Esc(versionId) + "\", " +
            "\"name\": \"Qinglan Testseed\", \"files\": [" + string.Join(",", filesJson) + "], " +
            "\"dependencies\": { \"minecraft\": \"" + mcVersion + "\", \"fabric-loader\": \"" + loader + "\" } }";
        var ie = zip.CreateEntry("modrinth.index.json");
        using (var w = ie.Open())
            w.Write(Encoding.UTF8.GetBytes(index));
        return outputZip;
    }

    /// <summary>做一个带 fabric.mod.json 内嵌元数据的"mod"文件。</summary>
    public static byte[] ModWithFabricJson(string name, string version)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("fabric.mod.json");
            using var w = e.Open();
            w.Write(Encoding.UTF8.GetBytes("{\"name\": \"" + name + "\", \"version\": \"" + version + "\"}"));
        }
        return ms.ToArray();
    }
}
