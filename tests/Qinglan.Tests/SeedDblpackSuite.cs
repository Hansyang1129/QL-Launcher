using Qinglan.Launcher.Core;
using System.IO.Compression;
using System.Text;

namespace Qinglan.Tests;

/// <summary>种子导入（mrpack 安装 + QL 化）+ dblpack 清单语义。</summary>
internal static class SeedDblpackSuite
{
    public static void Run()
    {
        SeedizeFullLike();
        SeedizeLiteLikeThenSync();
        DblpackMetadata();
        MrpackValidation();
    }

    /// <summary>FULL 包语义：所有内容都在 overrides → seeded 全中，后续 sync 只补停用频道。</summary>
    private static void SeedizeFullLike()
    {
        var man = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A_CONTENT", (string?)null),
            ("mods/B.jar", "B_CONTENT", (string?)"dh"),
        }, common: new[] { ("config/c1.toml", "c1") });

        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var manifest = QlManifest.FromJson(Fx.WithBaseUrl(man.Json, server.BaseUrl));
        // 注册 manifest 条目内容（种后 sync 补停用频道要用这些 URL）
        foreach (var (path, content) in man.Contents)
            server.AddFile("/" + path, Encoding.UTF8.GetBytes(content));

        var mrpack = Path.Combine(dir, "seed.mrpack");
        Fx.BuildMrpack(mrpack, server, new List<(string, string)>(), new[]
        {
            ("mods/A.jar", "A_CONTENT"),
            ("mods/B.jar.disabled", "B_CONTENT"),
            ("config/c1.toml", "c1"),
        });

        var install = QlSeedImporter.InstallMrpackAsync(mrpack, dir, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        T.CheckEq("FULL 安装 overrides=3", 3, install.OverridesCopied);
        T.CheckEq("FULL 安装无需 CDN 下载", 0, install.FilesDownloaded);
        T.Check("versionId 供实例取名（内置 -full 待剥）",
            install.VersionId == "Qinglan-Test 1.0-full");

        var seeded = QlSeedImporter.Seedize(dir, manifest, "https://example.test/manifest.json", "std",
            new Dictionary<string, bool> { ["dh"] = false });
        T.CheckEq("seeded=3（B 以禁用名落位、c1 命中）", 3, seeded.Seeded);
        T.CheckEq("missing=0 mismatch=0", (0, 0), (seeded.Missing, seeded.Mismatch));
        VerifySeeded(dir, manifest, expectBDisabled: true);

        var outcome = QlSyncManager.Sync(dir, manifest, "std");
        T.CheckEq("种后 sync 补停用频道 pfm 2 个", 2, outcome.Downloaded);
        T.CheckEq("活跃侧 3 条 unchanged", 3, outcome.Unchanged);
    }

    /// <summary>LITE 包语义：overrides 少一截，余量 CDN-first → 缺下载时 seedize 报 missing，一轮 sync 精准补齐。</summary>
    private static void SeedizeLiteLikeThenSync()
    {
        var man = Fx.Manifest(new[]
        {
            ("mods/A.jar", "A_CONTENT", (string?)null),
            ("mods/B.jar", "B_CONTENT", (string?)null),
        });
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var manifest = QlManifest.FromJson(Fx.WithBaseUrl(man.Json, server.BaseUrl));
        foreach (var (path, content) in man.Contents)
            server.AddFile("/" + path, Encoding.UTF8.GetBytes(content));

        var mrpack = Path.Combine(dir, "lite.mrpack");
        Fx.BuildMrpack(mrpack, server, new List<(string, string)> { ("mods/A.jar", "A_CONTENT") },
            new[] { ("mods/B.jar", "B_CONTENT") });

        var install = QlSeedImporter.InstallMrpackAsync(mrpack, dir, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        T.CheckEq("LITE overrides=1", 1, install.OverridesCopied);
        T.CheckEq("LITE CDN 下载=1", 1, install.FilesDownloaded);

        var seeded = QlSeedImporter.Seedize(dir, manifest, null, "std");
        T.CheckEq("seeded=2", 2, seeded.Seeded);
        T.CheckEq("missing=0（A 已被安装环节补齐）", 0, seeded.Missing);

        // 安装环节下载失败（模拟 CDN 缺席）：seedize 必须 missing=1 且整体不中断
        var dir2 = Fx.TempDir();
        using var server2 = MiniHttpServer.StartAsync(dir2).GetAwaiter().GetResult();
        foreach (var (path, content) in man.Contents)
            server2.AddFile("/" + path, Encoding.UTF8.GetBytes(content));
        server2.FailPath("/mods/A.jar");
        var manifest2 = QlManifest.FromJson(Fx.WithBaseUrl(man.Json, server2.BaseUrl));
        var mrpack2 = Path.Combine(dir2, "lite.mrpack");
        Fx.BuildMrpack(mrpack2, server2, new List<(string, string)> { ("mods/A.jar", "A_CONTENT") },
            new[] { ("mods/B.jar", "B_CONTENT") });
        var install2 = QlSeedImporter.InstallMrpackAsync(mrpack2, dir2, null, CancellationToken.None)
            .GetAwaiter().GetResult();
        T.CheckEq("CDN 404 时 filesFailed=1", 1, install2.FilesFailed);
        var seeded2 = QlSeedImporter.Seedize(dir2, manifest2, null, "std");
        T.CheckEq("seeded=1（B）", 1, seeded2.Seeded);
        T.CheckEq("missing=1（A 等同步）", 1, seeded2.Missing);

        // 修复服务器后一轮 sync 精准补：std 的 A + 停用频道的 A/B
        server2.RemoveFail("/mods/A.jar");
        var sync = QlSyncManager.Sync(dir2, manifest2, "std");
        T.CheckEq("sync 补 3 个", 3, sync.Downloaded);
        T.CheckEq("补齐后 std tracked=2", 2, QlSyncState.Load(dir2).TrackedOf("std").Count);
    }

    private static void VerifySeeded(string dir, QlManifest manifest, bool expectBDisabled)
    {
        var state = QlSyncState.Load(dir);
        T.Check("种子写入状态版本", state.PackVersion == manifest.PackVersionOrDefault());
        T.Check("种子记录 manifestUrl", state.ManifestUrl == "https://example.test/manifest.json");
        T.Check("种子后 std 有 tracked", state.TrackedOf("std").ContainsKey("mods/A.jar"));
        var bName = expectBDisabled ? "B.jar.disabled" : "B.jar";
        T.Check("B 落位名 = " + bName, File.Exists(Path.Combine(dir, "mods", bName)));
        T.Check("标记已被盖", QlDirectoryLayout.ReadChannelMarker(Path.Combine(dir, "mods")) == "std");
        T.Check("dblpack 出场", File.Exists(Path.Combine(dir, "dblpack.json")));
    }

    /// <summary>fabric.mod.json 让 dblpack 携带 mod 名与版本；无内嵌信息的条目保持匿名。</summary>
    private static void DblpackMetadata()
    {
        var gameDir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(gameDir).GetAwaiter().GetResult();

        // Fabric.jar：二进制 jar 带 fabric.mod.json
        var bytes = Fx.ModWithFabricJson("Cool Mod", "3.2");
        server.AddFile("/mods/Fabric.jar", bytes);

        var fabricEntry =
            "{ \"path\": \"mods/Fabric.jar\", \"sha1\": \"" + Fx.Sha1(bytes) + "\", \"size\": " +
            bytes.Length + ", \"urls\": [\"" + server.BaseUrl + "mods/Fabric.jar\"] }";
        var plainText = "PLAIN_CONTENT";
        var plainEntry =
            "{ \"path\": \"mods/Cool.jar\", \"sha1\": \"" + Fx.Sha1(plainText) + "\", \"size\": " +
            Fx.SizeOf(plainText) + ", \"urls\": [\"" + server.BaseUrl + "mods/Cool.jar\"] }";
        server.AddFile("/mods/Cool.jar", Encoding.UTF8.GetBytes(plainText));

        var json =
            "{ \"schema\": 1, \"pack\": {\"name\":\"n\",\"version\":\"v\"}, \"channels\": { " +
            "\"std\": {\"label\":\"l\",\"mods\":[" + fabricEntry + "," + plainEntry + "],\"files\":[]}, " +
            "\"pfm\": {\"label\":\"l\",\"mods\":[],\"files\":[]}, " +
            "\"common\": {\"label\":\"l\",\"mods\":[],\"files\":[]} } }";
        var manifest = QlManifest.FromJson(json);
        QlSyncManager.Sync(gameDir, manifest, "std");

        var dbl = QlDblPack.Load(gameDir)!;
        var fabricMod = dbl.Mods.First(m => m.Path == "mods/Fabric.jar");
        T.Check("fabric.mod.json 名字被解析", fabricMod.Name == "Cool Mod");
        T.Check("fabric.mod.json 版本被解析", fabricMod.Version == "3.2");
        T.Check("无内嵌信息的条目不给名字", dbl.Mods.First(m => m.Path == "mods/Cool.jar").Name == null);
    }

    /// <summary>坏 mrpack 必须给清晰 IOException。</summary>
    private static void MrpackValidation()
    {
        var dir = Fx.TempDir();
        using var server = MiniHttpServer.StartAsync(dir).GetAwaiter().GetResult();
        var bad = Path.Combine(dir, "bad.mrpack");
        using (var fs = File.Create(bad))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            zip.CreateEntry("random.txt");
        try
        {
            QlSeedImporter.InstallMrpackAsync(bad, dir, null, CancellationToken.None).GetAwaiter().GetResult();
            T.Check("缺 index 的 mrpack 被拒", false);
        }
        catch (IOException e)
        {
            T.Check("缺 index 的 mrpack 被拒", e.Message.Contains("modrinth.index.json"));
        }
    }
}
