namespace Qinglan.Launcher.Core.Diagnostics;

/// <summary>数据驱动的归因规则：模式匹配 + 可选退出码，按 <see cref="Priority"/> 从小到大命中。</summary>
public sealed record QlErrorRule(
    string Id,
    string Category,
    int Priority,
    string[] Patterns,
    int[] ExitCodes,
    string Title,
    string Conclusion,
    string[] Suggestions);

/// <summary>本地错误归因规则表；只做字符串匹配，不联网、不上报。</summary>
public static class QlErrorRules
{
    private static readonly QlErrorRule UnknownRule = new(
        "unknown", "未知", 99, Array.Empty<string>(), Array.Empty<int>(),
        "未能确定具体原因",
        "游戏异常退出，启动器没有匹配到已知的日志特征。",
        new[]
        {
            "展开「查看原始日志」查看最后几行。",
            "把 data/launcher.log 复制给维护者。",
        });

    /// <summary>兜底规则（永远最后命中）。</summary>
    public static QlErrorRule Unknown => UnknownRule;

    public static IReadOnlyList<QlErrorRule> All { get; } = new[]
    {
        new QlErrorRule(
            "java-missing", "Java", 1,
            new[]
            {
                "cannot find the file", "系统找不到指定的文件", "未找到 Java",
                "Java 运行时可执行文件不存在", "CreateProcess error=2",
                "The system cannot find the file specified",
            },
            Array.Empty<int>(),
            "找不到 Java 运行时",
            "启动器没有找到可执行的 java.exe，游戏进程无法启动。",
            new[]
            {
                "在设置页选择包含 bin\\java.exe 的目录。",
                "清空 Java 目录，让启动器自动下载运行时后重试。",
            }),

        new QlErrorRule(
            "java-version", "Java", 2,
            new[]
            {
                "UnsupportedClassVersionError", "class file version", "requires Java",
                "has been compiled by a more recent version", "IncompatibleClassChangeError",
                "major version 65", "major version 61",
            },
            Array.Empty<int>(),
            "Java 版本不匹配",
            "当前 Java 版本无法运行这个 Minecraft 或整合包。",
            new[]
            {
                "在设置页指定整合包要求的 Java 17 或 Java 21。",
                "让启动器自动下载对应 Mojang 运行时后重试。",
            }),

        new QlErrorRule(
            "oom", "内存", 3,
            new[]
            {
                "OutOfMemoryError", "Java heap space", "GC overhead limit exceeded",
                "Could not reserve enough space for object heap", "Invalid maximum heap size",
                "Too small maximum heap", "Invalid initial heap size",
                "unable to create new native thread", "Direct buffer memory",
                "Native memory allocation",
            },
            Array.Empty<int>(),
            "内存不足",
            "Java 堆或本机内存不足，游戏无法继续运行。",
            new[]
            {
                "把内存上限降到 4096–8192 MB，或检查 -Xmx 是否超过物理内存。",
                "关闭其他占内存程序后重试；必要时增加物理内存。",
            }),

        new QlErrorRule(
            "mod-conflict", "模组", 4,
            new[]
            {
                "Incompatible mod set", "ModResolutionException", "Duplicate mod",
                "DuplicateMod", "Missing dependencies", "requires any version of",
                "NoSuchMethodError", "ClassNotFoundException", "Mixin apply failed",
                "EntrypointNotFoundException", "fabric-loader",
            },
            Array.Empty<int>(),
            "模组冲突或依赖缺失",
            "模组之间不兼容、重复，或缺少前置依赖。",
            new[]
            {
                "先移除最近添加的 mod，确认能启动后再逐个加回。",
                "到同步页重新应用整合包，确保依赖完整。",
            }),

        new QlErrorRule(
            "download-network", "网络", 5,
            new[]
            {
                "启动文件下载失败", "资源索引下载失败", "timed out", "timeout",
                "SocketTimeoutException", "ConnectException", "UnknownHostException",
                "SSLHandshakeException", "HttpRequestException", "Connection reset",
                "No route to host", " 404 ", " 403 ", " 502 ", " 503 ",
            },
            Array.Empty<int>(),
            "启动文件下载失败",
            "准备启动文件时网络请求失败或超时。",
            new[]
            {
                "检查网络或代理后重试。",
                "到同步页重新同步一次整合包。",
            }),

        new QlErrorRule(
            "auth", "登录", 6,
            new[]
            {
                "QlAuthException", "invalid_grant", "AADSTS", "Unauthorized",
                "401 Unauthorized", "refresh token", "登录已失效",
                "authentication failed", "token expired", "invalid token",
            },
            Array.Empty<int>(),
            "登录状态失效",
            "微软账号令牌无效或已过期。",
            new[]
            {
                "回到首页重新登录微软账号。",
                "或先「退出登录」，用离线账号启动。",
            }),

        new QlErrorRule(
            "native-crash", "原生崩溃", 7,
            new[]
            {
                "EXCEPTION_ACCESS_VIOLATION", "EXCEPTION_STACK_OVERFLOW",
                "A fatal error has been detected by the Java Runtime Environment",
                "hs_err_pid", "SIGSEGV",
            },
            new[] { -1073741819, -1073741571, -1073740940 },
            "Java 原生崩溃",
            "游戏或 JVM 在原生层崩溃，常见于显卡驱动或渲染类模组。",
            new[]
            {
                "更新显卡驱动后重试。",
                "先移除渲染/光影类模组，确认稳定后再逐个加回。",
            }),

        UnknownRule,
    };
}
