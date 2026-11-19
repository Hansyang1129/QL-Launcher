using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Launch;

namespace Qinglan.Tests;

/// <summary>R2 JVM 参数：分词、-Xmx 去重、log4j 覆盖、内存校验、换行展示与默认等价。</summary>
internal static class JvmArgsSuite
{
    public static void Run()
    {
        T.Group("R2 JVM：空参数与默认值");
        EmptyAndDefault();
        T.Group("R2 JVM：用户参数与 -Xmx 去重");
        UserOptions();
        T.Group("R2 JVM：非法输入");
        InvalidInputs();
        T.Group("R2 JVM：换行展示往返");
        PrettyRoundTrip();
        T.Group("R2 JVM：命令行拼装");
        Assembly();
        T.Group("R2 JVM：本地持久化");
        Persistence();
        T.Group("R2 内存：启动前整理");
        T.Check("TrimAll 不抛异常", QlMemoryOptimizer.TrimAll(50) >= 0);
    }

    private static void EmptyAndDefault()
    {
        T.Check("空参数可解析", QlJvmOptions.TryParse(null, 4096, out var plan, out var error));
        T.Check("空参数无错误", error is null);
        T.CheckEq("空参数 -Xmx", "-Xmx4096M", plan.XmxToken);
        T.CheckEq("空参数无额外 token", 0, plan.UserTokens.Count);
        T.Check("空参数不标记用户 -Xmx", !plan.UserProvidedXmx);
    }

    private static void UserOptions()
    {
        T.Check("用户参数可解析",
            QlJvmOptions.TryParse("-Xmx4G -Dfoo=bar", 8192, out var plan, out _));
        T.Check("用户 -Xmx 被识别", plan.UserProvidedXmx);
        T.CheckEq("用户 -Xmx 生效", "-Xmx4G", plan.XmxToken);
        T.CheckEq("用户 -Xmx 换算 MB", 4096, plan.XmxMb);
        T.CheckEq("用户参数顺序保留", "-Dfoo=bar", plan.UserTokens[0]);

        T.Check("分开写 -Xmx 也可解析",
            QlJvmOptions.TryParse("-Xmx 4G", 8192, out var split, out _));
        T.CheckEq("分开写合并为一个 token", "-Xmx4G", split.XmxToken);

        T.Check("无单位 -Xmx 按 MB 处理",
            QlJvmOptions.TryParse("-Xmx4096", 8192, out var unitless, out _));
        T.CheckEq("无单位补 M", "-Xmx4096M", unitless.XmxToken);

        T.Check("用户 log4j 覆盖可解析",
            QlJvmOptions.TryParse("-Dlog4j.configurationFile=\"C:\\a b\\log4j.xml\"", 4096,
                out var log4j, out _));
        T.Check("log4j 标记为用户覆盖", log4j.UserProvidedLog4j);
        T.CheckEq("引号内空格保留", "-Dlog4j.configurationFile=C:\\a b\\log4j.xml",
            log4j.UserTokens[0]);
    }

    private static void InvalidInputs()
    {
        T.Check("带换行被拒", !QlJvmOptions.TryParse("-Dfoo=1\n-Dbar=2", 4096, out _, out _));
        T.Check("引号未闭合被拒", !QlJvmOptions.TryParse("-Dfoo=\"bar", 4096, out _, out _));
        T.Check("注入字符被拒", !QlJvmOptions.TryParse("-Dfoo=1 & calc", 4096, out _, out _));
        T.Check("多个 -Xmx 被拒",
            !QlJvmOptions.TryParse("-Xmx4G -Xmx8G", 4096, out _, out _));
        T.Check("非法 -Xmx 被拒", !QlJvmOptions.TryParse("-Xmx0", 4096, out _, out _));
        T.Check("裸 log4j 被拒",
            !QlJvmOptions.TryParse("-Dlog4j.configurationFile", 4096, out _, out _));

        T.Check("空内存合法", QlJvmOptions.TryParseMemory("", out int autoMb, out _));
        T.CheckEq("空内存回落到自动", 0, autoMb);
        T.Check("数字内存合法", QlJvmOptions.TryParseMemory("8192", out int memoryMb, out _));
        T.CheckEq("数字内存值", 8192, memoryMb);
        T.Check("负数内存被拒", !QlJvmOptions.TryParseMemory("-1", out _, out _));
        T.Check("字母内存被拒", !QlJvmOptions.TryParseMemory("8G", out _, out _));
        T.Check("超大内存被拒", !QlJvmOptions.TryParseMemory("1048577", out _, out _));
    }

    private static void PrettyRoundTrip()
    {
        const string raw = "-Xmx4G -Dfoo=\"a b\" -Dbar=1";
        string pretty = QlJvmOptions.FormatPretty(raw);
        T.Check("顶层空格变成换行", pretty.Contains("-Xmx4G\n", StringComparison.Ordinal));
        T.Check("引号内空格保留在一行", pretty.Contains("\"a b\"", StringComparison.Ordinal));
        T.Check("两行以上", pretty.Split('\n').Length >= 2);

        string back = QlJvmOptions.FormatRaw(pretty);
        T.Check("还原后可分词",
            QlJvmOptions.TryTokenize(back, out var tokens, out _));
        T.CheckEq("往返 token 数", 3, tokens.Count);
        T.CheckEq("往返首 token", "-Xmx4G", tokens[0]);
        T.CheckEq("往返引号 token", "-Dfoo=a b", tokens[1]);
    }

    private static void Assembly()
    {
        var prep = Prep();
        var profile = QlSession.Offline("Steve");

        var defaults = QlArgsBuilder.Build(prep, "game", profile, 4096);
        T.CheckEq("默认首项 -Xmx", "-Xmx4096M", defaults[0]);
        T.CheckEq("默认只有一条 -Xmx", 1,
            defaults.Count(a => a.StartsWith("-Xmx", StringComparison.Ordinal)));
        T.Check("默认仍注入 log4j",
            defaults.Contains("-Dlog4j.configurationFile=log4j.xml"));
        T.Check("默认主类在 log4j 之后",
            defaults.IndexOf(prep.MainClass) > defaults.IndexOf("-Dlog4j.configurationFile=log4j.xml"));

        var custom = QlArgsBuilder.Build(prep, "game", profile, 4096, "-Dfoo=bar");
        T.Check("用户参数在主类之前", custom.IndexOf("-Dfoo=bar") < custom.IndexOf(prep.MainClass));
        T.CheckEq("用户参数仍只有一条 -Xmx", 1,
            custom.Count(a => a.StartsWith("-Xmx", StringComparison.Ordinal)));

        var userXmx = QlArgsBuilder.Build(prep, "game", profile, 4096, "-Xmx2G");
        T.CheckEq("用户 -Xmx 唯一", 1,
            userXmx.Count(a => a.StartsWith("-Xmx", StringComparison.Ordinal)));
        T.CheckEq("用户 -Xmx 在首位", "-Xmx2G", userXmx[0]);

        var userLog = QlArgsBuilder.Build(prep, "game", profile, 4096,
            "-Dlog4j.configurationFile=custom.xml");
        T.CheckEq("用户 log4j 不重复", 1,
            userLog.Count(a => a.StartsWith("-Dlog4j.configurationFile=", StringComparison.Ordinal)));
        T.Check("用户 log4j 值生效",
            userLog.Contains("-Dlog4j.configurationFile=custom.xml"));

        var expanded = QlArgsBuilder.ExpandJvmTemplate(prep.JvmArgs);
        T.CheckEq("展开版本模板保留占位符",
            "-Djava.library.path=${natives_directory}", expanded[0]);
        string copied = QlJvmOptions.JoinTokens(expanded);
        var withoutVersion = QlArgsBuilder.Build(prep, "game", profile, 4096, copied, false);
        T.CheckEq("Mojang 默认复制后 token 数一致", defaults.Count, withoutVersion.Count);
        T.Check("Mojang 默认复制后 token 序列一致", defaults.SequenceEqual(withoutVersion));

        var keepUnknown = QlArgsBuilder.Build(prep, "game", profile, 4096, "-Dfoo=${unknown}");
        T.Check("未知占位符不被静默吞掉", keepUnknown.Contains("-Dfoo=${unknown}"));
    }

    private static void Persistence()
    {
        var dir = Fx.TempDir();
        new QlConfig
        {
            MaxMemoryMb = 8192,
            JvmArgs = "-Dfoo=bar",
            JvmArgsOnePerLine = true,
        }.Save(dir);
        var cfg = QlConfig.Load(dir);
        T.CheckEq("JvmArgs 往返", "-Dfoo=bar", cfg.JvmArgs);
        T.Check("一行偏好往返", cfg.JvmArgsOnePerLine);
        T.CheckEq("内存往返", 8192, cfg.MaxMemoryMb);

        var defaultDir = Fx.TempDir();
        File.WriteAllText(Path.Combine(defaultDir, "config.json"),
            "{\"maxMemoryMb\":4096}");
        T.Check("缺省使用版本 JSON JVM 参数", QlConfig.Load(defaultDir).UseVersionJvmArgs);

        var customDir = Fx.TempDir();
        new QlConfig { UseVersionJvmArgs = false }.Save(customDir);
        T.Check("关闭版本 JVM 参数可往返", !QlConfig.Load(customDir).UseVersionJvmArgs);

        var uiDir = Fx.TempDir();
        new QlConfig
        {
            WindowWidth = 1280,
            WindowHeight = 800,
            OptimizeMemoryBeforeLaunch = true,
        }.Save(uiDir);
        var uiCfg = QlConfig.Load(uiDir);
        T.CheckEq("窗口宽度往返", 1280, uiCfg.WindowWidth);
        T.CheckEq("窗口高度往返", 800, uiCfg.WindowHeight);
        T.Check("启动前内存优化开关往返", uiCfg.OptimizeMemoryBeforeLaunch);
        T.CheckEq("窗口默认宽度", 1040, new QlConfig().WindowWidth);
        T.CheckEq("窗口默认高度", 680, new QlConfig().WindowHeight);
    }

    private static QlPrepResult Prep()
        => new()
        {
            VersionId = "test",
            MainClass = "net.minecraft.Main",
            GameJar = "game.jar",
            Classpath = new List<string> { "a.jar" },
            NativesDir = "natives",
            AssetsDir = "assets",
            AssetIndexName = "5",
            JavaMajor = 17,
            JvmArgs = new List<QlArg> { new QlArg.Token("-Djava.library.path=${natives_directory}") },
            GameArgs = new List<QlArg>
            {
                new QlArg.Token("--username"),
                new QlArg.Token("${auth_player_name}"),
            },
            LogConfigFile = "log4j.xml",
        };
}
