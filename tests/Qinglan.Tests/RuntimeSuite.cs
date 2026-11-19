using System.Text;

namespace Qinglan.Tests;

/// <summary>微软运行时下载引擎：清单路由、缺才下、SHA-1 校验、bin/java.exe 定位。</summary>
internal static class RuntimeSuite
{
    public static void Run()
    {
        T.Group("运行时下载 · 版本路由");
        Routing();
        T.Group("运行时下载 · 装配全流程");
        InstallFlow().GetAwaiter().GetResult();
        T.Group("运行时下载 · 仅 LZMA 拒装");
        LzmaOnlyFlow().GetAwaiter().GetResult();
    }

    private static void Routing()
    {
        T.CheckEq("17.0.8_7 的大版本", 17, Qinglan.Launcher.Core.Launch.QlMojangRuntime.ParseNameVersion("17.0.8_7"));
        T.CheckEq("openjdk-17.0.8 前置杂文也认", 17,
            Qinglan.Launcher.Core.Launch.QlMojangRuntime.ParseNameVersion("openjdk-17.0.8"));
        T.CheckEq("25-ea+36 取首段", 25,
            Qinglan.Launcher.Core.Launch.QlMojangRuntime.ParseNameVersion("25-ea+36"));
        T.CheckEq("乱序也认 java-8u402", 8,
            Qinglan.Launcher.Core.Launch.QlMojangRuntime.ParseNameVersion("java-8u402"));
        T.Check("无数字给 -1", Qinglan.Launcher.Core.Launch.QlMojangRuntime.ParseNameVersion("gamma") == -1);
        T.Check("windows-x64 平台标", Qinglan.Launcher.Core.Launch.QlMojangRuntime.PlatformName() == "windows-x64");
    }

    private static async Task InstallFlow()
    {
        var server = await MiniHttpServer.StartAsync(Fx.TempDir())
            .ConfigureAwait(false);
        var installDir = Path.Combine(Fx.TempDir(), "runtimes", "java-runtime-gamma");
        try
        {
            var javaContent = "fake java.exe 载荷"u8.ToArray();
            var exeEntry = "java-runtime-gamma/windows-x64/java-runtime-gamma/bin/java.exe";
            var libGood = "java-runtime-gamma/windows-x64/java-runtime-gamma/lib/good.jar";
            var skipKey = "java-runtime-gamma/windows-x64/java-runtime-gamma/lib/skip.jar";

            server.AddFile("/f/java", javaContent);
            server.AddFile("/f/good", Encoding.UTF8.GetBytes("good"));
            server.AddFile("/f/skip", Encoding.UTF8.GetBytes("skip"));
            server.AddFile("/list", Encoding.UTF8.GetBytes(
                """
                {"downloads":{"windows-x64":{"java-runtime-gamma":[
                  {"version":{"name":"17.0.8_7"},"manifest":{"url":"__M17__"}},
                  {"version":{"name":"16.0.26"},"manifest":{"url":"__M16__"}}
                ]}}}
                """.Replace("__M17__", server.BaseUrl + "mani-17").Replace("__M16__", server.BaseUrl + "mani-16")));
            server.AddFile("/mani-16", Encoding.UTF8.GetBytes("""{"files":{}}"""));
            server.AddFile("/mani-17", Encoding.UTF8.GetBytes(
                """
                {"files":{
                  "__JAVA__":{"type":"file","executable":true,"downloads":{"raw":{"url":"__JAVAURL__","sha1":"__JAVASHA__","size":__JAVASIZE__}}},
                  "__GOOD__":{"type":"file","executable":false,"downloads":{"raw":{"url":"__GOODURL__","sha1":"__GOODSHA__","size":__GOODSIZE__}}},
                  "__SKIP__":{"type":"file","executable":false,"downloads":{"raw":{"url":"__SKIPURL__","sha1":"__SKIPSHA__","size":__SKIPSIZE__}}},
                  "__DIR__":{"type":"directory"}
                }}
                """.Replace("__JAVA__", exeEntry).Replace("__JAVAURL__", server.BaseUrl + "f/java")
                    .Replace("__JAVASHA__", Fx.Sha1(javaContent))
                    .Replace("__JAVASIZE__", javaContent.Length.ToString())
                    .Replace("__GOOD__", libGood).Replace("__GOODURL__", server.BaseUrl + "f/good")
                    .Replace("__GOODSHA__", Fx.Sha1("good"))
                    .Replace("__GOODSIZE__", Fx.SizeOf("good").ToString())
                    .Replace("__SKIP__", skipKey).Replace("__SKIPURL__", server.BaseUrl + "f/skip")
                    .Replace("__SKIPSHA__", Fx.Sha1("skip"))
                    .Replace("__SKIPSIZE__", Fx.SizeOf("skip").ToString())
                    .Replace("__DIR__", "java-runtime-gamma/windows-x64/java-runtime-gamma/other")));

            // 路由：首个 >=17 的候选应选中 17.0.8_7，16 被跳过
            var (verName, manifestUrl) = await Qinglan.Launcher.Core.Launch.QlMojangRuntime
                .ResolveAsync("java-runtime-gamma", 17, server.BaseUrl + "list")
                .ConfigureAwait(false);
            T.CheckEq("候选路由选 17.0.8_7", "17.0.8_7", verName);
            T.Check("manifest 指向 17 的清单", manifestUrl.EndsWith("mani-17"));

            // 预置：skip.jar 预先就位（内容正确）→ 不该再被请求
            var skipLocal = Path.Combine(installDir, skipKey);
            Directory.CreateDirectory(Path.GetDirectoryName(skipLocal)!);
            await File.WriteAllBytesAsync(skipLocal, Encoding.UTF8.GetBytes("skip"));

            var bad = Path.Combine(installDir, libGood);
            Directory.CreateDirectory(Path.GetDirectoryName(bad)!);
            await File.WriteAllBytesAsync(bad, Encoding.UTF8.GetBytes("WRONGCONTENT")); // 校验不符 → 重下

            var home = await Qinglan.Launcher.Core.Launch.QlMojangRuntime.InstallAsync(
                "java-runtime-gamma", server.BaseUrl + "mani-17", installDir)
                .ConfigureAwait(false);

            T.Check("Home 指向包根（java.exe 的两级上层）", home.EndsWith("java-runtime-gamma"));
            T.Check("java.exe 落位", File.Exists(Path.Combine(home, "bin", "java.exe")));
            T.CheckEq("重下载后内容为正确载荷", Encoding.UTF8.GetString(javaContent),
                await File.ReadAllTextAsync(Path.Combine(home, "bin", "java.exe")).ConfigureAwait(false));
            T.CheckEq("lib/good.jar 重下成功", "good",
                await File.ReadAllTextAsync(Path.Combine(installDir, libGood)).ConfigureAwait(false));
            T.Check("就位文件未被重新请求", server.Hits("/f/skip") == 0);
            T.Check("目录条目已创建",
                Directory.Exists(Path.Combine(installDir, "java-runtime-gamma/windows-x64/java-runtime-gamma/other")));
            T.Check("java.exe 请求了恰好一次", server.Hits("/f/java") == 1);
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task LzmaOnlyFlow()
    {
        var server = await MiniHttpServer.StartAsync(Fx.TempDir())
            .ConfigureAwait(false);
        var installDir = Path.Combine(Fx.TempDir(), "runtimes", "java-runtime-gamma-lzma");
        try
        {
            server.AddFile("/mani-lzma", Encoding.UTF8.GetBytes(
                """
                {"files":{
                  "jrg/bin/java.exe":{"type":"file","executable":true,"downloads":{"lzma":{"url":"http://x/l","sha1":"bb","size":1}}}
                }}
                """));
            Exception? caught = null;
            try
            {
                await Qinglan.Launcher.Core.Launch.QlMojangRuntime.InstallAsync(
                    "java-runtime-gamma", server.BaseUrl + "mani-lzma", installDir)
                    .ConfigureAwait(false);
            }
            catch (Exception e) { caught = e; }

            T.Check("仅 LZMA 条目装配失败", caught is IOException);
            T.Check("报错点名文件", caught!.Message.Contains("LZMA") && caught.Message.Contains("java.exe"));
        }
        finally
        {
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }
}
