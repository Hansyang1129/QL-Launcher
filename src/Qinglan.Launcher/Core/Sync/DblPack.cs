using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Qinglan.Launcher.Core;

/// <summary>实例内容清单（dblpack.json）：按条目清点 mod（含解析到的元数据）与资源包/光影。</summary>
/// <para>manifest 只谈文件（名字 + SHA-1），dblpack 谈条目：资源包没有内嵌版本信息，
/// 文件名是唯一的可移植线索，按 best-effort 正则解析。</para>
public sealed class QlDblPack
{
    public const int SchemaVersion = 1;

    /// <summary>解析不到版本时的占位（HMCL-QL 用 "?"）。</summary>
    public const string VersionUnknown = "?";

    [JsonPropertyName("schema")] public int Schema { get; set; } = SchemaVersion;
    [JsonPropertyName("pack")] public PackInfo? Pack { get; set; }
    [JsonPropertyName("generatedAt")] public string? GeneratedAt { get; set; }
    [JsonPropertyName("mods")] public List<ModEntry> Mods { get; set; } = new();
    [JsonPropertyName("resourcepacks")] public List<ItemEntry> Resourcepacks { get; set; } = new();
    [JsonPropertyName("shaders")] public List<ItemEntry> Shaders { get; set; } = new();

    public sealed class PackInfo
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
    }

    public sealed class ModEntry
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("filename")] public string? Filename { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
        [JsonPropertyName("optional")] public string? Optional { get; set; }
        [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    }

    public sealed class ItemEntry
    {
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("filename")] public string? Filename { get; set; }
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("sha1")] public string? Sha1 { get; set; }
    }

    /// <summary>按当前 manifest 与磁盘现状生成清单；磁盘上没有的条目跳过。</summary>
    public static QlDblPack Scan(string gameDir, QlManifest manifest, string activeChannel,
        IReadOnlySet<string> enabledOptional)
    {
        var dbl = new QlDblPack
        {
            Pack = new PackInfo
            {
                Name = manifest.PackNameOrDefault(),
                Version = manifest.PackVersionOrDefault(),
            },
            GeneratedAt = DateTime.UtcNow.ToString("o"),
        };

        foreach (var channel in QlManifest.ModsChannels)
        {
            var folder = QlDirectoryLayout.ModsDirectory(gameDir, channel, activeChannel);
            if (!Directory.Exists(folder)) continue;
            foreach (var entry in manifest.ChannelOrThrow(channel).Mods)
            {
                var raw = RawFileName(entry);
                var mod = new ModEntry
                {
                    Path = entry.Path,
                    Sha1 = entry.Sha1,
                    Optional = entry.Optional,
                };
                var enabled = string.IsNullOrWhiteSpace(entry.Optional) || enabledOptional.Contains(entry.Optional!);
                var plain = Path.Combine(folder, raw);
                var parked = Path.Combine(folder, raw + QlConstants.DisabledSuffix);
                // 禁用的可选 mod 只有当它停在禁用名下才算数。
                string? found = enabled
                    ? File.Exists(plain) ? plain : File.Exists(parked) ? parked : null
                    : File.Exists(parked) ? parked : null;
                if (found == null) continue;
                mod.Filename = Path.GetFileName(found);
                mod.Disabled = found == parked;
                ReadModMetadata(found, mod);
                dbl.Mods.Add(mod);
            }
        }

        foreach (var entry in manifest.ChannelOrThrow(QlManifest.ChannelCommon).Files)
        {
            var logical = entry.Path!;
            if (!logical.StartsWith("resourcepacks/") && !logical.StartsWith("shaderpacks/")) continue;
            var target = Path.Combine(gameDir, logical.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target)) continue;
            var item = new ItemEntry
            {
                Path = logical,
                Sha1 = entry.Sha1,
                Filename = Path.GetFileName(target),
            };
            item.Version = VersionFromFileName(item.Filename!);
            if (logical.StartsWith("resourcepacks/")) dbl.Resourcepacks.Add(item);
            else dbl.Shaders.Add(item);
        }
        return dbl;
    }

    /// <summary>读实例的清单；没有就是 null。</summary>
    public static QlDblPack? Load(string gameDir)
    {
        var file = Path.Combine(gameDir, QlConstants.DblpackFile);
        if (!File.Exists(file)) return null;
        try
        {
            var dbl = JsonSerializer.Deserialize<QlDblPack>(File.ReadAllText(file));
            if (dbl == null) return null;
            dbl.Mods ??= new();
            dbl.Resourcepacks ??= new();
            dbl.Shaders ??= new();
            return dbl;
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Globals.Log("dblpack 读取失败(忽略): " + e.Message);
            return null;
        }
    }

    /// <summary>清单落盘。</summary>
    public void Save(string gameDir)
        => File.WriteAllText(Path.Combine(gameDir, QlConstants.DblpackFile),
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

    /// <summary>读 jar/zip 内嵌的 mod 元数据（fabric.mod.json 优先，其次 mcmod.info）；失败不动结果。</summary>
    private static void ReadModMetadata(string file, ModEntry mod)
    {
        try
        {
            using var zip = ZipFile.OpenRead(file);
            var fabric = zip.GetEntry("fabric.mod.json");
            if (fabric != null)
            {
                using var s = fabric.Open();
                var doc = JsonDocument.Parse(s);
                mod.Name = StringOrNull(doc.RootElement, "name", "id") ?? mod.Name;
                mod.Version = StringOrNull(doc.RootElement, "version") ?? mod.Version;
                return;
            }
            var legacy = zip.GetEntry("mcmod.info");
            if (legacy != null)
            {
                using var s = legacy.Open();
                var doc = JsonDocument.Parse(s);
                if (doc.RootElement.ValueKind == JsonValueKind.Array
                    && doc.RootElement.GetArrayLength() > 0
                    && doc.RootElement[0].ValueKind == JsonValueKind.Object)
                {
                    mod.Name = StringOrNull(doc.RootElement[0], "name", "modid") ?? mod.Name;
                    mod.Version = StringOrNull(doc.RootElement[0], "version") ?? mod.Version;
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 无内嵌信息（zip 坏或缺条目都当匿名条目处理，不阻断清单生成）。
            Globals.Log("mod 元数据解析失败(忽略): " + file + " -> " + e.Message);
        }
    }

    private static string? StringOrNull(JsonElement obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()))
                return v.GetString();
        }
        return null;
    }

    private static readonly Regex DottedTail = new("([0-9]+(?:\\.[0-9]+){1,4})[a-zA-Z0-9]*$", RegexOptions.Compiled);
    private static readonly Regex VPrefix = new("[vV][_-]?([0-9]+(?:\\.[0-9]+)*)", RegexOptions.Compiled);

    /// <summary>从文件名尾巴抠版本，e.g. Faithful_32x_v1.2.1.zip → 1.2.1；抠不到为 null。</summary>
    public static string? VersionFromFileName(string filename)
    {
        var stem = Regex.Replace(filename, "\\.[^./]+$", "");
        if (stem.EndsWith(")")) stem = stem[..^1];
        var m = DottedTail.Match(stem);
        if (m.Success) return m.Groups[1].Value;
        m = VPrefix.Match(stem);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string RawFileName(QlManifest.FileEntry entry)
    {
        var p = entry.Path ?? throw new ManifestException("manifest path missing");
        return p.Substring(p.IndexOf('/', 1) + 1);
    }
}
