namespace Qinglan.Launcher.Core.Launch;

/// <summary>一条 JVM/游戏参数：直给字符串，或带规则数组的对象（Mojang arguments 结构）。</summary>
public abstract record QlArg
{
    public sealed record Token(string Value) : QlArg;

    public sealed record Ruled(string[] Values, QlRule[] Rules) : QlArg;
}

/// <summary>Mojang 规则：对当前 OS/架构/feature 逐条判定，末次匹配为准；列表非空而无一匹配 → 排除。</summary>
/// <param name="Action">allow / disallow。</param>
/// <param name="OsName">"" = 不限。</param>
/// <param name="OsArch">"" = 不限（Mojang 记法 x86 = 全部主流 Windows）。</param>
/// <param name="Features">要求 launcher 声明的特性集合 == 这里的真值。</param>
public sealed record QlRule(string Action, string OsName, string OsArch, Dictionary<string, bool>? Features)
{
    public bool OsMatches(string currentOs, string currentArch)
        => (string.IsNullOrEmpty(OsName) || currentOs.Equals(OsName, StringComparison.OrdinalIgnoreCase))
           && (string.IsNullOrEmpty(OsArch) || currentArch.Equals(OsArch, StringComparison.OrdinalIgnoreCase));

    public bool FeaturesMatch(IReadOnlyDictionary<string, bool>? current)
    {
        if (Features is null || Features.Count == 0) return true;
        if (current is null) return false;
        return Features.All(kv =>
            current.TryGetValue(kv.Key, out var have) && have == kv.Value);
    }
}

/// <summary>规则表求值：last-match-wins，默认否定（HMCL 同语义）。</summary>
public static class QlRules
{
    public static bool Evaluate(QlRule[]? rules, string osName, string osArch,
        IReadOnlyDictionary<string, bool>? features)
    {
        if (rules is null || rules.Length == 0) return true;
        var result = false; // 有一条以上规则却全不命中 → 排除
        foreach (var r in rules)
            if (r.OsMatches(osName, osArch) && r.FeaturesMatch(features))
                result = r.Action.Equals("allow", StringComparison.OrdinalIgnoreCase);
        return result;
    }
}

/// <summary>统一后的库条目：落盘路径相对 libraries/，URL 候选与校验（vanilla/fabric 两种形态都归一）。</summary>
public sealed record QlLibEntry(
    string Name, string Path, string[] Urls, string Sha1, long Size,
    bool IsNativeWindows, QlRule[]? Rules);

/// <summary>版本 JSON（vanilla 或 fabric profile）的要点还原。</summary>
public sealed class QlVersionJson
{
    public string Id { get; set; } = "";
    public string MainClass { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public List<QlLibEntry> Libraries { get; } = new();
    public List<QlArg> JvmArgs { get; } = new();
    public List<QlArg> GameArgs { get; } = new();

    /// <summary>所需 Java 大版本；fabric profile 不带（合并时用 vanilla 的）。</summary>
    public int JavaMajor { get; set; }

    public string? AssetIndexId { get; set; }
    public string? AssetIndexUrl { get; set; }
    public string? AssetIndexSha1 { get; set; }
    public long AssetIndexSize { get; set; } = -1;

    public string? ClientUrl { get; set; }
    public string? ClientSha1 { get; set; }
    public long ClientSize { get; set; }

    public string? LogFileId { get; set; }
    public string? LogFileUrl { get; set; }
    public string? LogFileSha1 { get; set; }
    public long LogFileSize { get; set; } = -1;

    // ───────────────── 解析 ─────────────────

    /// <summary>vanilla / fabric-meta 两种 json 一锅端（缺 arguments 的远古版本不支持，明确报错）。</summary>
    public static QlVersionJson Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var v = new QlVersionJson();

        v.Id = StrOr(root, "id") ?? throw new IOException("版本 JSON 缺少 id");
        v.MainClass = StrOr(root, "mainClass") ?? "";
        if (root.TryGetProperty("inheritsFrom", out var ih)) v.InheritsFrom = ih.GetString();
        if (root.TryGetProperty("javaVersion", out var jv))
            v.JavaMajor = jv.TryGetProperty("majorVersion", out var mj) ? mj.GetInt32() : 0;

        if (root.TryGetProperty("downloads", out var dl) && dl.TryGetProperty("client", out var cl))
        {
            v.ClientUrl = cl.GetProperty("url").GetString();
            v.ClientSha1 = cl.GetProperty("sha1").GetString();
            v.ClientSize = cl.TryGetProperty("size", out var cs) ? cs.GetInt64() : -1;
        }

            if (root.TryGetProperty("assetIndex", out var ai))
        {
            v.AssetIndexId = ai.GetProperty("id").GetString();
            v.AssetIndexUrl = ai.GetProperty("url").GetString();
            v.AssetIndexSha1 = StrOr(ai, "sha1");
            v.AssetIndexSize = ai.TryGetProperty("size", out var asz) ? asz.GetInt64() : -1;
        }

        if (root.TryGetProperty("logging", out var log) && log.TryGetProperty("client", out var lc)
            && lc.TryGetProperty("file", out var lf))
        {
            v.LogFileId = lf.GetProperty("id").GetString();
            v.LogFileUrl = lf.GetProperty("url").GetString();
            v.LogFileSha1 = StrOr(lf, "sha1");
            v.LogFileSize = lf.TryGetProperty("size", out var lsz) ? lsz.GetInt64() : -1;
        }

        if (root.TryGetProperty("libraries", out var libs))
            foreach (var le in libs.EnumerateArray())
                v.Libraries.Add(ParseLib(le));

        var hasArgs = root.TryGetProperty("arguments", out var ar);
        if (hasArgs)
        {
            ParseArgList(ar.GetProperty("game"), v.GameArgs);
            ParseArgList(ar.GetProperty("jvm"), v.JvmArgs);
        }
        if ((!hasArgs || v.GameArgs.Count == 0) && root.TryGetProperty("minecraftArguments", out var margs))
            // 远古结构的兜底通例：老版本这里逻辑独立，v1 明确不支持。
            throw new IOException("仅支持 arguments 结构的版本 JSON（1.9+fabric），" + v.Id + " 用了 minecraftArguments");
        if (!hasArgs)
            throw new IOException("版本 JSON 缺少 arguments: " + v.Id);

        return v;
    }

    private static QlArg ParseArg(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String)
            return new QlArg.Token(e.GetString() ?? "");
        var rules = e.TryGetProperty("rules", out var rs) ? ParseRules(rs) : Array.Empty<QlRule>();
        string[] values;
        if (e.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
            values = vals.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        else if (e.TryGetProperty("value", out var val))
            values = val.ValueKind == JsonValueKind.String
                ? new[] { val.GetString() ?? "" }
                : val.EnumerateArray().Select(x => x.GetString() ?? "").ToArray();
        else
            values = Array.Empty<string>();
        return new QlArg.Ruled(values, rules);
    }

    private static QlRule[] ParseRules(JsonElement rules)
    {
        var list = new List<QlRule>();
        foreach (var r in rules.EnumerateArray())
        {
            var action = StrOr(r, "action") ?? "allow"; // 宽容：feature 型规则可能不写 action
            string osName = "", osArch = "";
            if (r.TryGetProperty("os", out var os))
            {
                osName = StrOr(os, "name") ?? "";
                osArch = StrOr(os, "arch") ?? "";
            }
            Dictionary<string, bool>? feats = null;
            if (r.TryGetProperty("features", out var f) && f.ValueKind == JsonValueKind.Object)
            {
                feats = new Dictionary<string, bool>(StringComparer.Ordinal);
                foreach (var p in f.EnumerateObject()) feats[p.Name] = p.Value.ValueKind == JsonValueKind.True;
            }
            list.Add(new QlRule(action, osName, osArch, feats));
        }
        return list.ToArray();
    }

    private static void ParseArgList(JsonElement e, List<QlArg> sink)
    {
        foreach (var el in e.EnumerateArray()) sink.Add(ParseArg(el));
    }

    /// <summary>单条库：vanilla 带 downloads.artifact（权威路径/URL），fabric-meta 用 maven name + 基地址拼。</summary>
    private static QlLibEntry ParseLib(JsonElement le)
    {
        var name = le.GetProperty("name").GetString() ?? "";
        var (group, artifact, version, classifier) = SplitName(name);
        string path, sha1;
        long size = -1;
        string[] urls;
        if (le.TryGetProperty("downloads", out var dws) && dws.TryGetProperty("artifact", out var art))
        {
            path = art.GetProperty("path").GetString() ?? MavenPath(group, artifact, version, classifier);
            var url = art.GetProperty("url").GetString() ?? "";
            urls = string.IsNullOrWhiteSpace(url) ? Array.Empty<string>() : new[] { url };
            sha1 = StrOr(art, "sha1") ?? "";
            size = art.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
        }
        else
        {
            // fabric-meta：{name, url=maven 基地址, sha1, size}——无 downloads。
            path = MavenPath(group, artifact, version, classifier);
            var @base = StrOr(le, "url") ?? "";
            urls = new[] { @base.TrimEnd('/') + "/" + path };
            sha1 = StrOr(le, "sha1") ?? "";
            size = le.TryGetProperty("size", out var s) ? s.GetInt64() : -1;
        }
        var isNative = classifier.StartsWith("natives-windows", StringComparison.Ordinal);
        var rules = le.TryGetProperty("rules", out var rs) ? ParseRules(rs) : Array.Empty<QlRule>();
        return new QlLibEntry(name, path, urls, sha1, size, isNative, rules.Length == 0 ? null : rules);
    }

    // ───────────────── 合并 ─────────────────

    /// <summary>vanilla ⊕ fabric：fabric mainClass / 自己的 args；库表并集按路径去重（先到先得）。</summary>
    public static QlVersionJson Merge(QlVersionJson vanilla, QlVersionJson fabric)
    {
        var m = new QlVersionJson
        {
            Id = fabric.Id,
            MainClass = !string.IsNullOrEmpty(fabric.MainClass) ? fabric.MainClass : vanilla.MainClass,
            InheritsFrom = fabric.InheritsFrom ?? vanilla.Id,
            JavaMajor = vanilla.JavaMajor,
            AssetIndexId = vanilla.AssetIndexId,
            AssetIndexUrl = vanilla.AssetIndexUrl,
            AssetIndexSha1 = vanilla.AssetIndexSha1,
            AssetIndexSize = vanilla.AssetIndexSize,
            ClientUrl = vanilla.ClientUrl,
            ClientSha1 = vanilla.ClientSha1,
            ClientSize = vanilla.ClientSize,
            LogFileId = vanilla.LogFileId,
            LogFileUrl = vanilla.LogFileUrl,
            LogFileSha1 = vanilla.LogFileSha1,
            LogFileSize = vanilla.LogFileSize,
        };

        // 库：fabric 在前（loader 解析优先），同名同路径的以先出现者为准。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in fabric.Libraries.Concat(vanilla.Libraries))
            if (seen.Add(lib.Path)) m.Libraries.Add(lib);

        // 游戏参数：fabric 往往是空数组，此时用 vanilla 的（实测 fabric 0.20.1 正是如此）。
        var game = fabric.GameArgs.Count > 0 ? fabric.GameArgs : vanilla.GameArgs;
        m.GameArgs.AddRange(game);

        // JVM 参数：fabric 的排前面（如 -DFabricMcEmu），vanilla 模板（含 -cp ${classpath}）随后。
        var jvmSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in fabric.JvmArgs.Concat(vanilla.JvmArgs))
        {
            var key = a is QlArg.Token t ? "s:" + t.Value : "r:" + string.Join('|', (a as QlArg.Ruled)!.Values);
            if (jvmSeen.Add(key)) m.JvmArgs.Add(a);
        }
        return m;
    }

    // ───────────────── maven 坐标 ─────────────────

    public static (string Group, string Artifact, string Version, string Classifier) SplitName(string name)
    {
        var parts = name.Split(':');
        return parts.Length switch
        {
            >= 4 => (parts[0], parts[1], parts[2], parts[3]),
            3 => (parts[0], parts[1], parts[2], ""),
            _ => throw new IOException("无法解析的库坐标: " + name),
        };
    }

    public static string MavenPath(string group, string artifact, string version, string classifier)
        => group.Replace('.', '/')
            + "/" + artifact + "/" + version + "/"
            + artifact + "-" + version + (string.IsNullOrEmpty(classifier) ? "" : "-" + classifier) + ".jar";

    public static string MavenPathFromName(string name)
    {
        var (g, a, v, c) = SplitName(name);
        return MavenPath(g, a, v, c);
    }

    private static string? StrOr(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
