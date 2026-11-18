namespace Qinglan.Launcher.Core.Launch;

/// <summary>把版本 JSON 的参数模板翻译成可直接喂给 java.exe 的 argv（离线资料齐备后全部变量可替换）。</summary>
public static class QlArgsBuilder
{
    public static List<string> Build(QlPrepResult prep, string gameDir, QlProfile profile, long maxMemoryMb)
    {        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
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

        var args = new List<string> { "-Xmx" + maxMemoryMb + "M" };
        Append(args, prep.JvmArgs, vars);

        // log4j 是 JVM 选项，必须落在主类之前（Mojang logging.client.argument 的实际形态）。
        if (!string.IsNullOrEmpty(prep.LogConfigFile))
            args.Add("-Dlog4j.configurationFile=" + prep.LogConfigFile);

        // 主类处于 JVM 参数与游戏参数之间——少了这一句，--username 会被 java 当成 JVM 选项。
        args.Add(prep.MainClass);
        Append(args, prep.GameArgs, vars);

        // 空值的可选对（--clientId/--xuid，离线没有这俩）整对剔除。放在两边模板拼完之后。
        StripEmptyOptionals(args, "--clientId", "--xuid");
        return args;
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
