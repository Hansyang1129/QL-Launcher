using Qinglan.Launcher.Core;

namespace Qinglan.Tests;

/// <summary>manifest 严格校验 / RPU 归属 / 可选组 / 状态文件语义。</summary>
internal static class ManifestStateSuite
{
    public static void Run()
    {
        T.Group("manifest 严格校验");
        ValidManifestParses();
        InvalidManifestsThrow();
        OptionalGroupsAndEffective();
        NormalizeChannel();
        RpuOwnership();

        T.Group("状态文件 ql-manifest-state.json");
        StateRoundtrip();
        CorruptStateParked();
        ReplaceTrackedDropsEmpty();
        AtomicSave();

        T.Group("dblpack versionFromFileName");
        VersionParsing();
    }

    private static void ValidManifestParses()
    {
        var data = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A", (string?)"dh"),
            ("mods/B.jar", "B", (string?)null),
        }, common: new[] { ("config/x.toml", "x") });
        var m = QlManifest.FromJson(data.Json);
        // 历史的 pfm 频道允许出现（只读兼容），但必需频道只有 std + common。
        T.Check("schema/通道齐备的 manifest 可解析", m.Schema == 1 && m.Channels!.Count == 3);
        T.Check("pack 默认值", m.PackNameOrDefault() == "Qinglan-Test" && m.PackVersionOrDefault() == "1.0.0-test");
    }

    private static void InvalidManifestsThrow()
    {
        var good = Fx.Manifest(new[] { ("mods/A.jar", "A", (string?)null) });
        var baseJson = good.Json;

        var badSchema = baseJson.Replace("\"schema\": 1", "\"schema\": 2");
        T.Check("schema 不符被拒", ThrowsManifest(badSchema.Replace("URLPLACEHOLDER", "http://x/")));

        var noCommon = baseJson.Replace("\"common\"", "\"commonX\"");
        T.Check("缺 common 被拒", ThrowsManifest(Fx.WithBaseUrl(noCommon, "http://x/")));

        // 新版只认单一 mods 频道：缺 std 的 manifest（哪怕是 pfm-only）也被拒。
        var noStd = "{ \"schema\": 1, \"pack\": {\"name\":\"n\",\"version\":\"v\"}, \"channels\": { " +
                    "\"pfm\": {\"label\":\"l\",\"mods\":[],\"files\":[]}, " +
                    "\"common\": {\"label\":\"l\",\"mods\":[],\"files\":[]} } }";
        T.Check("缺 std（pfm-only）被拒", ThrowsManifest(noStd));

        var unsafePath = baseJson.Replace("mods/A.jar", "../mods/A.jar");
        T.Check("路径越界被拒", ThrowsManifest(Fx.WithBaseUrl(unsafePath, "http://x/")));

        // 渠道内重复 path：std 两条同路径条目必须被拒
        var entry = "{ \"path\": \"mods/Dup.jar\", \"sha1\": \"" + Fx.Sha1("D") + "\", \"size\": 1, \"urls\": [\"http://x/D.jar\"] }";
        var dupJson = "{ \"schema\": 1, \"pack\": {\"name\":\"n\",\"version\":\"v\"}, \"channels\": { " +
                      "\"std\": {\"label\":\"l\",\"mods\":[" + entry + "," + entry + "],\"files\":[]}, " +
                      "\"pfm\": {\"label\":\"l\",\"mods\":[],\"files\":[]}, " +
                      "\"common\": {\"label\":\"l\",\"mods\":[],\"files\":[]} } }";
        T.Check("渠道内重复 path 被拒", ThrowsManifest(dupJson));

        var noSha = "{ \"schema\": 1, \"pack\": {\"name\":\"n\",\"version\":\"v\"}, \"channels\": { " +
                    "\"std\": {\"label\":\"l\",\"mods\":[" + entry.Replace("mods/Dup.jar", "mods/X.jar")
                        .Replace("\"sha1\": \"" + Fx.Sha1("D") + "\", ", "") + "],\"files\":[]}, " +
                    "\"common\": {\"label\":\"l\",\"mods\":[],\"files\":[]} } }";
        T.Check("缺 sha1 被拒", ThrowsManifest(noSha));

        var negSize = noSha.Replace("\"size\": 1", "\"size\": -1").Replace("mods/X.jar", "mods/Y.jar");
        T.Check("负 size 被拒", ThrowsManifest(negSize));

        var noSize = negSize.Replace("\"size\": -1, ", "").Replace("mods/Y.jar", "mods/Z.jar");
        T.Check("sizeless 被拒（发布器永不产出 sizeless）", ThrowsManifest(noSize));
    }

    private static bool ThrowsManifest(string json)
    {
        try
        {
            QlManifest.FromJson(json);
            return false;
        }
        catch (ManifestException)
        {
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine("   非 ManifestException: " + e.GetType().Name + ": " + e.Message);
            return false;
        }
    }

    private static void OptionalGroupsAndEffective()
    {
        var data = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A", (string?)"dh"),
            ("mods/B.jar", "B", (string?)"axiom"),
            ("mods/C.jar", "C", (string?)null),
        });
        var m = QlManifest.FromJson(data.Json);
        var groups = m.OptionalGroups();
        T.Check("可选组声明序 = dh, axiom",
            groups[0] == "dh" && groups[1] == "axiom" && groups.Count == 2);

        var on = new HashSet<string> { "dh" };
        var eff = m.EffectiveEntries("std", on);
        T.Check("enabledOptional 含 dh → A/C 生效",
            eff.Any(e => e.Path == "mods/A.jar") && eff.Any(e => e.Path == "mods/C.jar") &&
            !eff.Any(e => e.Path == "mods/B.jar"));
    }

    private static void NormalizeChannel()
    {
        T.Check("std 归一成自身", QlManifest.NormalizeChannel("STD") == "std");
        // 新版不再有 pfm 频道：未知/历史值一律落 common。
        T.Check("pfm 已无独立频道 → common", QlManifest.NormalizeChannel("pfm") == "common");
        T.Check("未知频道归一 common", QlManifest.NormalizeChannel("xyz") == "common");
    }

    private static void RpuOwnership()
    {
        T.Check("qlrpu/ 前缀归 RPU", QlManifest.IsRpuOwned("qlrpu/cache/current.json"));
        T.Check("qlrpu 根文件归 RPU", QlManifest.IsRpuOwned("qlrpu/x.json"));
        T.Check("RPU 设置文件归 RPU", QlManifest.IsRpuOwned("config/qlrpu-client.json"));
        // 用户口径：resourcepacks 是 mrpack 管的，不再归 RPU。
        T.Check("resourcepacks 归 mrpack 同步", !QlManifest.IsRpuOwned("resourcepacks/r1.zip"));
        T.Check("config 其他文件归启动器", !QlManifest.IsRpuOwned("config/create-server.toml"));
        T.Check("null 不归 RPU", !QlManifest.IsRpuOwned(null));
        T.Check("反斜杠路径归一后判定", QlManifest.IsRpuOwned("qlrpu\\cache\\current.json"));
    }

    private static void StateRoundtrip()
    {
        var dir = Fx.TempDir();
        var state = new QlSyncState
        {
            PackVersion = "26.8.1",
            PackUrl = "https://example.test/pack.mrpack",
            OptionalRecorded = true,
            // 遗留字段照存照读（只读兼容）。
            ManifestUrl = "https://example.test/manifest.json",
            ActiveChannel = "pfm",
        };
        state.EnabledOptional.Add("dh");
        state.Track("std", new Dictionary<string, string> { ["mods/A.jar"] = "aa", ["mods/B.jar"] = "bb" });
        state.Track("common", new() { ["config/x.toml"] = "cc" });
        state.Save(dir);

        var loaded = QlSyncState.Load(dir);
        T.Check("状态回读 packVersion", loaded.PackVersion == "26.8.1");
        T.Check("状态回读 packUrl", loaded.PackUrl == "https://example.test/pack.mrpack");
        T.Check("状态回读 optionalRecorded", loaded.OptionalRecorded);
        T.Check("状态回读可选组", loaded.IsOptionalEnabled("dh"));
        T.Check("遗留 manifestUrl 照读", loaded.ManifestUrl == "https://example.test/manifest.json");
        T.Check("tracked 嵌套键值完整",
            loaded.TrackedOf("std")["mods/A.jar"] == "aa" &&
            loaded.TrackedOf("common")["config/x.toml"] == "cc");
        T.Check("未记录频道给空表", loaded.TrackedOf("pfm").Count == 0);
    }

    private static void CorruptStateParked()
    {
        var dir = Fx.TempDir();
        var file = Path.Combine(dir, QlSyncState.StateFile);
        File.WriteAllText(file, "{ not json ");

        var state = QlSyncState.Load(dir);
        T.Check("坏状态 → 空态起步", state.PackVersion == null && state.Tracked.Count == 0);
        T.Check("坏状态挪成 .corrupt", File.Exists(file + ".corrupt") && !File.Exists(file));
    }

    private static void ReplaceTrackedDropsEmpty()
    {
        var state = new QlSyncState();
        state.ReplaceTracked(new Dictionary<string, Dictionary<string, string>>
        {
            ["std"] = new() { ["mods/A.jar"] = "a" },
            ["common"] = new(),
        });
        T.Check("ReplaceTracked 丢弃空频道", state.Tracked.ContainsKey("std") &&
            !state.Tracked.ContainsKey("common"));
    }

    private static void AtomicSave()
    {
        var dir = Fx.TempDir();
        var state = QlSyncState.Empty();
        state.PackVersion = "v1";
        state.Save(dir);
        T.Check("保存后无 .tmp 残留", !File.Exists(Path.Combine(dir, QlSyncState.StateFile + ".tmp")));
    }

    private static void VersionParsing()
    {
        T.Check("dotted 尾巴解析", QlDblPack.VersionFromFileName("Faithful_32x_v1.2.1.zip") == "1.2.1");
        T.Check("括号尾巴剥离", QlDblPack.VersionFromFileName("Pack (1.0).zip") == "1.0");
        T.Check("v_ 前缀解析", QlDblPack.VersionFromFileName("Shader_v_2.3.4.jar") == "2.3.4");
        T.Check("无版本返回 null", QlDblPack.VersionFromFileName("nofold.zip") == null);
    }
}
