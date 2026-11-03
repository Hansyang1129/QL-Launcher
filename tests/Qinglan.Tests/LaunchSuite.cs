using System.IO.Compression;
using Qinglan.Launcher.Core.Launch;

namespace Qinglan.Tests;

/// <summary>M2 启动链：版本合并 → 规划/下载 → natives/类路径 → 参数与身份 → 进程壳/Java 解析。</summary>
internal static class LaunchSuite
{
    public static void Run() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        T.Group("M2 版本合并");
        // ───────── 夹具 ───────── 全部照真实 1.20.1 + fabric 0.20.1 的形态做缩小版。
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        var B = srv.BaseUrl; // 以 / 结尾

        var clientBytes = Encoding.UTF8.GetBytes("QLFAKECLIENTJAR-1.20.1");
        var baseBytes = Encoding.UTF8.GetBytes("LIB-QINGLAN-BASE");
        var fabricBytes = Encoding.UTF8.GetBytes("FABRIC-LOADER-FAKE");
        var nativesWin = ZipOf(("f.dll", "FAKE-DLL-WINDOWS"));     // natives-windows 变体
        var nativesArm = ZipOf(("arm.dll", "FAKE-DLL-ARM64"));     // natives-windows-arm64 变体
        var aBytes = Encoding.UTF8.GetBytes("ASSET-STONE-SPRITE");
        var bBytes = Encoding.UTF8.GetBytes("ASSET-DIRT-BLOB-XXXX");
        var logBytes = Encoding.UTF8.GetBytes("<Configuration FAKE-LOG4J/>");
        var hA = Fx.Sha1(aBytes);
        var hB = Fx.Sha1(bBytes);

        srv.AddFile("/v/client.jar", clientBytes);
        srv.AddFile("/lib/base.jar", baseBytes);
        srv.AddFile("/lib/lwjglwin.jar", nativesWin);
        srv.AddFile("/lib/lwjglarm.jar", nativesArm);
        srv.AddFile("/m2/org/fake/fabric-loader/0.20.1/fabric-loader-0.20.1.jar", fabricBytes);
        srv.AddFile("/log/client-1.12.xml", logBytes);
        srv.AddFile("/res/" + hA[..2] + "/" + hA, aBytes);
        srv.AddFile("/res/" + hB[..2] + "/" + hB, bBytes);

        var assetIndexJson =
            "{ \"objects\": { \"textures/stone.png\": { \"hash\": \"" + hA + "\", \"size\": " + aBytes.Length + " }, " +
            "\"textures/dirt.png\": { \"hash\": \"" + hB + "\", \"size\": " + bBytes.Length + " } } }";
        srv.AddFile("/asset-index.json", Encoding.UTF8.GetBytes(assetIndexJson));

        string LibEntry(string name, string path, string urlFile, byte[] bytes, string rules)
        {
            var rule = string.IsNullOrEmpty(rules) ? "" : ", \"rules\": [" + rules + "]";
            return "{ \"name\": \"" + name + "\", \"downloads\": { \"artifact\": { \"path\": \"" + path +
                   "\", \"url\": \"" + B + urlFile + "\", \"sha1\": \"" + Fx.Sha1(bytes) + "\", \"size\": " +
                   bytes.Length + " } }" + rule + " }";
        }

        var vanJson =
            "{ \"id\": \"1.20.1\", \"type\": \"release\", \"mainClass\": \"net.minecraft.client.main.Main\", " +
            "\"javaVersion\": { \"majorVersion\": 17, \"component\": \"java-runtime-gamma\" }, " +
            "\"assetIndex\": { \"id\": \"5x\", \"sha1\": \"" + Fx.Sha1(assetIndexJson) +
            "\", \"size\": " + Fx.SizeOf(assetIndexJson) + ", \"totalSize\": 999, \"url\": \"" + B + "asset-index.json\" }, " +
            "\"downloads\": { \"client\": { \"url\": \"" + B + "v/client.jar\", \"sha1\": \"" + Fx.Sha1(clientBytes) +
            "\", \"size\": " + clientBytes.Length + " } }, " +
            "\"logging\": { \"client\": { \"argument\": \"-Dlog4j.configurationFile=${path}\", \"file\": " +
            "{ \"id\": \"client-1.12.xml\", \"sha1\": \"" + Fx.Sha1(logBytes) + "\", \"size\": " + logBytes.Length +
            ", \"url\": \"" + B + "log/client-1.12.xml\" }, \"type\": \"log4j2-xml\" } }, " +
            "\"arguments\": { \"jvm\": [ " +
            "{ \"value\": \"-XstartOnFirstThread\", \"rules\": [ { \"action\": \"allow\", \"os\": { \"name\": \"osx\" } } ] }, " +
            "{ \"value\": \"-XX:HeapDumpPath=ql.tile.heapdump\", \"rules\": [ { \"action\": \"allow\", \"os\": { \"name\": \"windows\" } } ] }, " +
            "{ \"value\": \"-Xss1M\", \"rules\": [ { \"action\": \"allow\", \"os\": { \"name\": \"\" } } ] }, " +
            "\"-Djava.library.path=${natives_directory}\", \"-Djna.tmpdir=${natives_directory}\", " +
            "\"-Dminecraft.launcher.brand=${launcher_name}\", \"-cp\", \"${classpath}\" ], " +
            "\"game\": [ \"--username\", \"${auth_player_name}\", \"--version\", \"${version_name}\", " +
            "\"--gameDir\", \"${game_directory}\", \"--assetsDir\", \"${assets_root}\", \"--assetIndex\", \"${assets_index_name}\", " +
            "\"--uuid\", \"${auth_uuid}\", \"--accessToken\", \"${auth_access_token}\", " +
            "\"--clientId\", \"${clientid}\", \"--xuid\", \"${auth_xuid}\", \"--userType\", \"${user_type}\", " +
            "{ \"value\": \"--demo\", \"rules\": [ { \"features\": { \"is_demo_user\": true } } ] }, " +
            "{ \"value\": [ \"--width\", \"${resolution_width}\" ], \"rules\": [ { \"features\": { \"has_custom_resolution\": true } } ] } ] }, " +
            "\"libraries\": [ " + string.Join(",", new[]
            {
                LibEntry("com.ql:base:1.0", "com/ql/base/1.0/base-1.0.jar", "lib/base.jar", baseBytes, ""),
                LibEntry("com.ql:base:1.0", "com/ql/base/1.0/base-1.0.jar", "lib/base.jar", baseBytes, ""),
                LibEntry("net.ql:netty-epoll:4.0:linux-x86_64",
                    "net/ql/netty-epoll/4.0/netty-epoll-4.0-linux-x86_64.jar", "lib/never-linux.jar",
                    Encoding.UTF8.GetBytes("LINUX-NATIVE"), "{ \"action\": \"allow\", \"os\": { \"name\": \"linux\" } }"),
                LibEntry("net.ql:lwjgl-glfw:3.0:natives-macos",
                    "net/ql/lwjgl-glfw/3.0/lwjgl-glfw-3.0-natives-macos.jar", "lib/never-macos.jar",
                    Encoding.UTF8.GetBytes("OSX-NATIVE"), "{ \"action\": \"allow\", \"os\": { \"name\": \"osx\" } }"),
                LibEntry("net.ql:lwjgl-glfw:3.0:natives-windows-arm64",
                    "net/ql/lwjgl-glfw/3.0/lwjgl-glfw-3.0-natives-windows-arm64.jar", "lib/lwjglarm.jar",
                    nativesArm, "{ \"action\": \"allow\", \"os\": { \"name\": \"windows\" } }"),
                LibEntry("net.ql:lwjgl-glfw:3.0:natives-windows",
                    "net/ql/lwjgl-glfw/3.0/lwjgl-glfw-3.0-natives-windows.jar", "lib/lwjglwin.jar",
                    nativesWin, "{ \"action\": \"allow\", \"os\": { \"name\": \"windows\" } }"),
            }) + " ] }";
        srv.AddFile("/v/1.20.1.json", Encoding.UTF8.GetBytes(vanJson));

        // fabric profile（真形态：无 downloads，url 是 maven 基地址；game args 空数组）。
        var fabJson =
            "{ \"id\": \"fabric-loader-0.20.1-1.20.1\", \"inheritsFrom\": \"1.20.1\", " +
            "\"releaseTime\": \"2026-11-02T00:00:00+0000\", \"time\": \"2026-11-02T00:00:00+0000\", \"type\": \"release\", " +
            "\"mainClass\": \"net.fabricmc.loader.impl.launch.knot.KnotClient\", " +
            "\"arguments\": { \"game\": [], \"jvm\": [ \"-DFabricMcEmu= net.minecraft.client.main.Main \" ] }, " +
            "\"libraries\": [ { \"name\": \"org.fake:fabric-loader:0.20.1\", \"url\": \"" + B + "m2/\", " +
            "\"md5\": \"nope\", \"sha1\": \"" + Fx.Sha1(fabricBytes) + "\", \"sha256\": \"nope\", \"sha512\": \"nope\", " +
            "\"size\": " + fabricBytes.Length + " } ] }";
        srv.AddFile("/fabric/profile.json", Encoding.UTF8.GetBytes(fabJson));

        var merge = await QlLaunchPrep.MergeAsync(B + "v/1.20.1.json", B + "fabric/profile.json");
        T.CheckEq("MainClass 取 fabric 的", "net.fabricmc.loader.impl.launch.knot.KnotClient", merge.MainClass);
        T.CheckEq("合并后的 Id", "fabric-loader-0.20.1-1.20.1", merge.Id);
        T.CheckEq("Java 大版本取 vanilla", 17, merge.JavaMajor);
        T.CheckEq("资源索引 id", "5x", merge.AssetIndexId);
        T.Check("重复条目去重（7 条原始→6 唯一）", merge.Libraries.Count == 6);
        T.Check("fabric 无 game args → 采用 vanilla 的", merge.GameArgs.Any(a =>
            a is QlArg.Token t && t.Value == "--username"));

        // ───────── 规划 + 下载 ─────────
        T.Group("M2 规划与下载");
        var gameDir = Path.Combine(Fx.TempDir(), "inst");
        Directory.CreateDirectory(gameDir);
        var tasks = await QlLaunchPrep.PlanAsync(merge, gameDir, B + "res/");
        T.CheckEq("计划任务数（客户端1+库3+log4j1+资源2）", 7, tasks.Count);
        T.Check("linux 库不进计划", !tasks.Any(t => t.Label.Contains("netty")));
        T.Check("osx natives 不进计划", !tasks.Any(t => t.Label.Contains("natives-macos")));
        var myClassifier = QlLaunchPrep.NativeClassifierForArch();
        var otherClassifier = myClassifier == "natives-windows" ? "natives-windows-arm64" : "natives-windows";
        T.Check("异构 natives 不进计划（" + otherClassifier + "）",
            !tasks.Any(t => t.Label.Contains(otherClassifier)));
        await QlLaunchPrep.DownloadAllAsync(tasks, null, CancellationToken.None);

        var jarPath = QlLaunchPrep.VersionJarPath(gameDir, merge.Id);
        T.Check("客户端 jar 落位", File.Exists(jarPath) &&
            Fx.Sha1(await File.ReadAllBytesAsync(jarPath)) == Fx.Sha1(clientBytes));
        var objA = Path.Combine(gameDir, "assets", "objects", hA[..2], hA);
        T.Check("资产对象 1 落位", File.Exists(objA) &&
            Fx.Sha1(await File.ReadAllBytesAsync(objA)) == hA);
        var objB = Path.Combine(gameDir, "assets", "objects", hB[..2], hB);
        T.Check("资产对象 2 落位", File.Exists(objB));
        T.Check("log4j 配置落位", File.Exists(Path.Combine(gameDir, "assets", "log_configs", "client-1.12.xml")));
        T.Check("资源索引落位", File.Exists(Path.Combine(gameDir, "assets", "indexes", "5x.json")));

        var tasks2 = await QlLaunchPrep.PlanAsync(merge, gameDir, B + "res/");
        T.CheckEq("幂等：第二次规划任务数", 0, tasks2.Count);

        // ───────── Finalize ─────────
        T.Group("M2 natives 与类路径");
        var prep = QlLaunchPrep.Finalize(merge, gameDir);
        T.CheckEq("类路径段数", 4, prep.Classpath.Count); // base + 本机 natives + fabric loader + client
        T.Check("类路径末段 = 客户端 jar", prep.Classpath[^1] == jarPath);
        T.Check("类路径含 fabric loader", prep.Classpath.Any(p => p.Contains("fabric-loader")));
        T.Check("类路径无 linux 库", !prep.Classpath.Any(p => p.Contains("netty")));
        var wantDll = myClassifier == "natives-windows" ? "f.dll" : "arm.dll";
        var notDll = myClassifier == "natives-windows" ? "arm.dll" : "f.dll";
        T.Check("natives 目录解出目标 dll（" + myClassifier + "）",
            File.Exists(Path.Combine(prep.NativesDir, wantDll)));
        T.Check("异构 native 未解出", !File.Exists(Path.Combine(prep.NativesDir, notDll)));

        // ───────── 身份与参数 ─────────
        T.Group("M2 身份与参数");
        var prof = QlSession.Offline("Steve");
        T.CheckEq("离线资料 UUID", QlSession.OfflineUuid("Steve"), prof.Uuid);
        T.CheckEq("同名永远同 UUID", QlSession.OfflineUuid("Steve"), QlSession.OfflineUuid("Steve"));
        T.Check("离线 UUID 是 v3", prof.Uuid[14] == '3');
        T.Check("离线 UUID 变体位 IETF", "89ab".Contains(prof.Uuid[19]));
        T.Check("不同名字不同 UUID", QlSession.OfflineUuid("Alex") != prof.Uuid);
        T.Check("离线 token 占位 0 / legacy", prof.AccessToken == "0" && prof.UserType == "legacy");

        var args = QlArgsBuilder.Build(prep, gameDir, prof, 1024);
        T.CheckEq("首个 JVM 参数", "-Xmx1024M", args[0]);
        T.Check("fabric JVM 参数在最前", args.Contains("-DFabricMcEmu= net.minecraft.client.main.Main "));
        T.Check("windows JVM 参数命中", args.Contains("-XX:HeapDumpPath=ql.tile.heapdump"));
        T.Check("全平台 JVM 参数命中", args.Contains("-Xss1M"));
        T.Check("osx JVM 参数剔除", !args.Contains("-XstartOnFirstThread"));
        T.Check("natives 变量注入正确", args.Any(a => a == "-Djava.library.path=" + prep.NativesDir));
        T.Check("cp 在游戏参数之前",
            args.IndexOf("-cp") >= 0 && args.IndexOf("--username") > args.IndexOf("-cp"));
        int IdxOf(string flag) => args.IndexOf(flag);
        T.CheckEq("--username 值", "Steve", args[IdxOf("--username") + 1]);
        T.CheckEq("--version 值", "fabric-loader-0.20.1-1.20.1", args[IdxOf("--version") + 1]);
        T.CheckEq("--gameDir 值", gameDir, args[IdxOf("--gameDir") + 1]);
        T.CheckEq("--assetIndex 值", "5x", args[IdxOf("--assetIndex") + 1]);
        T.CheckEq("--uuid 值", prof.Uuid, args[IdxOf("--uuid") + 1]);
        T.CheckEq("--accessToken 离线占位", "0", args[IdxOf("--accessToken") + 1]);
        T.CheckEq("--userType", "legacy", args[IdxOf("--userType") + 1]);
        T.Check("clientId/xuid 成对剔除", !args.Contains("--clientId") && !args.Contains("--xuid"));
        T.Check("feature 参数不出现（demo/resolution）",
            !args.Contains("--demo") && !args.Any(a => a.StartsWith("--width")));
        T.Check("无残留变量", !args.Any(a => a.Contains("${")));
        T.Check("log4j 参数收尾", args[^1].StartsWith("-Dlog4j.configurationFile=") &&
            args[^1].Contains("log_configs"));

        // ───────── Java 解析 + 进程壳 ─────────
        T.Group("M2 Java 解析与进程壳");
        var (raw17, maj17) = QlJavaLocator.ParseVersionOutput("openjdk version \"17.0.9\" 2023-10-17");
        T.CheckEq("现代记法解析", "17.0.9", raw17);
        T.CheckEq("现代记法大版本", 17, maj17);
        var (raw8, maj8) = QlJavaLocator.ParseVersionOutput("java version \"1.8.0_302\"");
        T.CheckEq("老记法解析", "1.8.0_302", raw8);
        T.CheckEq("老记法折算大版本", 8, maj8);
        var (rawBad, _) = QlJavaLocator.ParseVersionOutput("一堆没有版本行的输出");
        T.Check("非版本行判不出", rawBad is null);

        var lines = new List<string>();
        var exitCode = int.MinValue;
        var comspec = Environment.ExpandEnvironmentVariables("%ComSpec%");
        using (var p = new QlGameProcess(comspec, new[] { "/d", "/s", "/c", "echo ql-proc-ok & exit 42" },
                   gameDir, l => { lock (lines) lines.Add(l); }, c => exitCode = c))
        {
            var t0 = DateTime.UtcNow;
            while (exitCode == int.MinValue && (DateTime.UtcNow - t0).TotalSeconds < 10)
                await Task.Delay(50);
        }
        T.CheckEq("退出码透传", 42, exitCode);
        T.Check("行流捕获", lines.Any(s => s.Contains("ql-proc-ok")));
    }

    private static byte[] ZipOf(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        foreach (var (name, content) in entries)
        {
            var e = zip.CreateEntry(name);
            using var w = e.Open();
            w.Write(Encoding.UTF8.GetBytes(content));
        }
        return ms.ToArray();
    }
}
