namespace Qinglan.Launcher.Core.Launch;

/// <summary>把版本 JSON 的参数模板翻译成可直接喂给 java.exe 的 argv（离线资料齐备后全部变量可替换）。</summary>
public static class QlArgsBuilder
{
    public static List<string> Build(QlPrepResult prep, string gameDir, QlProfile profile,
        long maxMemoryMb, string? extraJvmArgs = null, bool useVersionJvmArgs = true)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["natives_directory"] = prep.NativesDir,
            ["classpath"] = string.Join(Path.PathSeparator, prep.Classpath),
            ["launcher_name"] = Globals.ProductName,
            ["launcher_version"] = Globals.Version,
            ["version_name"] = prep.VersionId,
            ["game_directory"] = gameDir,
            ["assets_root"] = prep.AssetsDir,
            ["assets_index_name"] = prep.AssetIndexName,
            ["auth_player_name"] = profile.Name,
            ["auth_uuid"] = profile.Uuid,
            ["auth_access_token"] = profile.AccessToken,
            ["auth_xuid"] = "",
            ["clientid"] = "",
            ["user_type"] = profile.UserType,
            ["version_type"] = Globals.ProductName,
            ["path"] = prep.LogConfigFile ?? "",
        };

        int memoryMb = (int)Math.Clamp(maxMemoryMb, 1, QlJvmOptions.MaxMemoryMb);
        if (!QlJvmOptions.TryParse(extraJvmArgs, memoryMb, out var jvm, out string? jvmError))
            throw new ArgumentException(jvmError ?? "额外 JVM 参数无效。");

        // 最终 -Xmx 只保留一条：用户 JvmArgs > 内存字段；版本 JSON 自带的 -Xmx 被删除。
        var args = new List<string> { jvm.XmxToken };
        if (useVersionJvmArgs)
        {
            var versionJvm = new List<string>();
            Append(versionJvm, prep.JvmArgs, vars);
            RemoveXmx(versionJvm);
            args.AddRange(versionJvm);
        }

        // 用户额外参数放在版本 JSON 之后、主类之前；log4j 覆盖项也在这里。
        // 用户 token 也走变量替换：本服“Mojang 默认”按钮填的是 ${natives_directory} 等原始模板。
        // 未知占位符原样保留，避免用户参数被静默吞掉。
        foreach (string token in jvm.UserTokens) AddUser(args, token, vars);

        // log4j 是 JVM 选项，必须落在主类之前（Mojang logging.client.argument 的实际形态）。
        if (!jvm.UserProvidedLog4j && !string.IsNullOrEmpty(prep.LogConfigFile))
            args.Add("-Dlog4j.configurationFile=" + prep.LogConfigFile);

        // 主类处于 JVM 参数与游戏参数之间——少了这一句，--username 会被 java 当成 JVM 选项。
        args.Add(prep.MainClass);
        Append(args, prep.GameArgs, vars);

        // 空值的可选对（--clientId/--xuid，离线没有这俩）整对剔除。放在两边模板拼完之后。
        StripEmptyOptionals(args, "--clientId", "--xuid");
        return args;
    }

    /// <summary>删除版本 JSON 可能自带的 -Xmx（兼容 -Xmx 与 -Xmx4G 两种写法）。</summary>
    private static void RemoveXmx(List<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "-Xmx" && i + 1 < args.Count)
            {
                args.RemoveRange(i, 2);
                i--;
            }
            else if (args[i].StartsWith("-Xmx", StringComparison.Ordinal))
            {
                args.RemoveAt(i);
                i--;
            }
        }
    }

    /// <summary>
    /// 把版本 JSON 的 JVM 模板展开成原始 token（保留 ${...} 占位符），供设置页
    /// “Mojang 默认”按钮填入额外参数框；启动时再替换变量。
    /// </summary>
    public static List<string> ExpandJvmTemplate(IEnumerable<QlArg> template)
    {
        var result = new List<string>();
        foreach (var arg in template)
        {
            switch (arg)
            {
                case QlArg.Token token:
                    result.Add(token.Value);
                    break;
                case QlArg.Ruled ruled:
                    if (QlRules.Evaluate(ruled.Rules, "windows", QlLaunchPrep.ArchLabel(), null))
                        result.AddRange(ruled.Values);
                    break;
            }
        }
        return result;
    }

    private static void Append(List<string> sink, List<QlArg> template, Dictionary<string, string> vars)
    {
        if (template is null) return;
        foreach (var arg in template)
        {
            switch (arg)
            {
                case QlArg.Token t:
                    Add(sink, t.Value, vars);
                    break;
                case QlArg.Ruled r:
                    if (QlRules.Evaluate(r.Rules, "windows", QlLaunchPrep.ArchLabel(), null))
                        foreach (var value in r.Values) Add(sink, value, vars);
                    break;
            }
        }
    }

    /// <summary>变量替换；替换后仍残留 ${…} 的 token 整条丢弃（如未下 log4j 时模板里的 ${path}）。</summary>
    private static void Add(List<string> sink, string raw, Dictionary<string, string> vars)
    {
        var s = vars.Aggregate(raw, (acc, kv) => acc.Replace("${" + kv.Key + "}", kv.Value));
        if (s.Contains("${", StringComparison.Ordinal)) return;
        sink.Add(s);
    }

    /// <summary>用户参数用的变量替换：已知占位符展开，未知 ${…} 原样保留，不静默吞掉。</summary>
    private static void AddUser(List<string> sink, string raw, Dictionary<string, string> vars)
    {
        var s = vars.Aggregate(raw, (acc, kv) => acc.Replace("${" + kv.Key + "}", kv.Value));
        sink.Add(s);
    }

    private static void StripEmptyOptionals(List<string> args, params string[] flags)
    {
        var i = 0;
        while (i < args.Count - 1)
        {
            if (flags.Contains(args[i], StringComparer.Ordinal) && string.IsNullOrEmpty(args[i + 1]))
            {
                args.RemoveAt(i + 1);
                args.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }
}
