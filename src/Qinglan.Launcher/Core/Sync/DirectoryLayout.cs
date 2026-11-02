using System.Text;
using System.Text.Json;

namespace Qinglan.Launcher.Core;

/// <summary>游戏目录内按频道划分的 mods 文件夹布局。</summary>
/// <para>每个频道一个文件夹：当前活跃的叫 <c>mods</c>，其余停在 <c>mods-&lt;频道&gt;</c>。
/// 切频道只做改名，瞬时完成。每个文件夹内置隐藏标记文件（内容=频道名），
/// 于是即使没有任何启动器状态，磁盘也能自证哪个频道在活跃。频道切换的两次改
/// 名之间可以被打断，这里提供两种半僵尸状态的恢复。</para>
public static class QlDirectoryLayout
{
    /// <summary>游戏正在加载的 folder 名。</summary>
    public const string ActiveModsFolder = "mods";

    /// <summary>写进每个受管 mods 文件夹的隐藏标记文件；内容为频道名。</summary>
    public const string ChannelMarker = ".qinglan-channel";

    /// <summary>频道 folder 名：如 <c>mods-std</c>。</summary>
    public static string ParkedFolderName(string channel)
        => ActiveModsFolder + "-" + channel.ToLowerInvariant();

    /// <summary>频道 folder 的物理路径（activeChannel 是当前活跃频道时等于 mods/）。</summary>
    public static string ModsDirectory(string gameDir, string channel, string activeChannel)
        => channel.Equals(activeChannel, StringComparison.Ordinal)
            ? Path.Combine(gameDir, ActiveModsFolder)
            : Path.Combine(gameDir, ParkedFolderName(channel));

    /// <summary>写入（或刷新）mods 文件夹里的频道标记。</summary>
    public static void StampChannel(string modsFolder, string channel)
    {
        Directory.CreateDirectory(modsFolder);
        File.WriteAllText(Path.Combine(modsFolder, ChannelMarker), channel, new UTF8Encoding(false));
    }

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>读 mods 文件夹里的标记；无标记或不是合法频道名时为 <see langword="null"/>。</summary>
    public static string? ReadChannelMarker(string modsFolder)
    {
        var marker = Path.Combine(modsFolder, ChannelMarker);
        if (!File.Exists(marker)) return null;
        try
        {
            var channel = File.ReadAllText(marker, Utf8NoBom).Trim().ToLowerInvariant();
            return QlManifest.ModsChannels.Contains(channel) ? channel : null;
        }
        catch (IOException e)
        {
            Globals.Log("读频道标记失败 " + modsFolder + ": " + e.Message);
            return null;
        }
    }

    /// <summary>从 mods 文件夹的标记识别当前活跃频道。</summary>
    /// <param name="fallback">mods 文件夹在但无标记时给定值（如纯种子导入后）。</param>
    /// <returns>识别到的或 fallback；mods 文件夹不存在时为 <see langword="null"/>。</returns>
    public static string? DetectActiveChannel(string gameDir, string fallback)
    {
        var mods = Path.Combine(gameDir, ActiveModsFolder);
        if (!Directory.Exists(mods)) return null;
        return ReadChannelMarker(mods) ?? fallback;
    }

    /// <summary>把游戏目录治愈到 recordedActiveChannel 对应的一致布局。</summary>
    /// <para>mods 里的标记大于记录值：磁盘知道内容归属，陈旧的启动器记录必须让路，
    /// 否则会把频道盖糊。保证幂等；每次启动和每次 sync 前都可调用。</para>
    public static void Recover(string gameDir, string? recorded)
    {
        var activeChannel = recorded is not null && QlManifest.ModsChannels.Contains(recorded)
            ? recorded : "std";

        var active = Path.Combine(gameDir, ActiveModsFolder);
        bool modsExists = Directory.Exists(active);

        // 磁盘盖过记录：已打标记说明这个 folder 实际就是该频道。
        var marker = modsExists ? ReadChannelMarker(active) : null;
        if (marker != null && !marker.Equals(activeChannel, StringComparison.Ordinal))
        {
            Globals.Log("mods 标记为 " + marker + "，记录为 " + activeChannel + "，信任磁盘");
            activeChannel = marker;
        }

        bool activeParkedExists = Directory.Exists(Path.Combine(gameDir, ParkedFolderName(activeChannel)));

        if (modsExists && !activeParkedExists)
        {
            StampChannel(active, activeChannel);
            return;
        }

        if (!modsExists && activeParkedExists
            && ReadChannelMarker(Path.Combine(gameDir, ParkedFolderName(activeChannel))) != null)
        {
            // 被打断的切换：第一次改名已经发生，补第二次。
            MoveDirectory(Path.Combine(gameDir, ParkedFolderName(activeChannel)), active);
            return;
        }

        if (!modsExists && !activeParkedExists && HasAnyParkedFolder(gameDir))
        {
            // 记录与磁盘完全失联；优先抬升 preferred 的标记 folder，否则最后有标记的那个。
            var promote = FindPromotableParkedFolder(gameDir, activeChannel);
            if (promote != null)
            {
                MoveDirectory(promote, active);
                return;
            }
        }

        Directory.CreateDirectory(active);
        StampChannel(active, activeChannel);
    }

    /// <summary>切活跃频道：只改文件夹名并盖标记。</summary>
    public static void SwitchChannel(string gameDir, string from, string to)
    {
        if (from.Equals(to, StringComparison.Ordinal)) return;

        var active = Path.Combine(gameDir, ActiveModsFolder);
        var parkedFrom = Path.Combine(gameDir, ParkedFolderName(from));
        var parkedTo = Path.Combine(gameDir, ParkedFolderName(to));

        Directory.CreateDirectory(active);

        // 1. 把当前 active folder 收进它自己的频道名下。
        if (!IsSamePath(active, parkedFrom))
        {
            MoveDirectory(active, parkedFrom);
            StampChannel(parkedFrom, from);
        }

        // 2. 抬升目标频道 folder；没有就建空的（sync 引擎随后填内容）。
        if (Directory.Exists(parkedTo))
        {
            MoveDirectory(parkedTo, active);
            StampChannel(active, to);
        }
        else if (!Directory.Exists(active))
        {
            Directory.CreateDirectory(active);
            StampChannel(active, to);
        }
        else if (IsSamePath(parkedFrom, active))
        {
            // parkedFrom==active 改名失败才可能走进来：保留 folder，只换标记。
            StampChannel(active, to);
        }
    }

    private static bool HasAnyParkedFolder(string gameDir)
        => QlManifest.ModsChannels.Any(c => Directory.Exists(Path.Combine(gameDir, ParkedFolderName(c))));

    private static string? FindPromotableParkedFolder(string gameDir, string preferred)
    {
        (string Channel, string Path)? preferredFolder = null;
        (string Channel, string Path)? lastStampedFolder = null;
        foreach (var channel in QlManifest.ModsChannels)
        {
            var folder = Path.Combine(gameDir, ParkedFolderName(channel));
            if (!Directory.Exists(folder)) continue;
            if (channel.Equals(preferred, StringComparison.Ordinal)) preferredFolder = (channel, folder);
            if (ReadChannelMarker(folder) != null) lastStampedFolder = (channel, folder);
        }
        if (preferredFolder is { } pf && ReadChannelMarker(pf.Path) != null) return pf.Path;
        if (lastStampedFolder is { } ls) return ls.Path;
        return preferredFolder?.Path; // 无标记时也退回 preferred
    }

    private static bool IsSamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>整目录改名；目标已存在则报错（绝不混并内容）。</summary>
    private static void MoveDirectory(string source, string target)
    {
        if (IsSamePath(source, target)) return;
        if (Directory.Exists(target))
            throw new IOException("目标目录已存在，拒绝改名: " + source + " -> " + target);
        try
        {
            Directory.Move(source, target);
        }
        catch (IOException)
        {
            // 轻微延迟后的重试（分析仪器和杀软偶发占用 folder 句柄）。
            Thread.Sleep(50);
            if (Directory.Exists(target))
                throw;
            Directory.Move(source, target);
        }
        Globals.Log("目录已改名: " + source + " -> " + target);
    }
}
