using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Qinglan.Launcher.Core.Sync.Rpu;

/// <summary>RPU 缓存预热同步的阶段（与 qlrpu-client UpdateEngine.Stage 的简中标签一致）。</summary>
/// <para>Java 端还有 VERIFYING（明文完整认证与校验）阶段——那是游戏内挂载资源包时做的事，
/// 启动器预热缓存不解密明文，也不调 /keys，因此没有该阶段。</para>
public enum RpuSyncStage
{
    Initializing,
    Authorizing,
    Checking,
    Sources,
    Downloading,
    Ready,
}

/// <summary>阶段简中标签。</summary>
public static class RpuSyncStageText
{
    public static string Label(RpuSyncStage stage) => stage switch
    {
        RpuSyncStage.Initializing => "初始化",
        RpuSyncStage.Authorizing => "在线授权与获取发布",
        RpuSyncStage.Checking => "扫描本地文件",
        RpuSyncStage.Sources => "探测下载源",
        RpuSyncStage.Downloading => "下载缺失资源",
        RpuSyncStage.Ready => "校验完成",
        _ => stage.ToString(),
    };
}

/// <summary>一次进度上报（DoneBytes/TotalBytes 为密文字节；UI 侧需自行调度到 UI 线程）。</summary>
public sealed record RpuSyncProgress(
    RpuSyncStage Stage,
    string Label,
    string Detail,
    long DoneBytes,
    long TotalBytes,
    int FilesDone,
    int FilesTotal);

/// <summary>一次同步的总账。</summary>
public sealed record RpuSyncResult(
    string? ReleaseId,
    int FilesTotal,
    int ValidFiles,
    int Downloaded,
    int CleanedStale,
    bool NothingToDownload,
    bool Canceled);

/// <summary>
/// RPU 缓存预热引擎：会话 → 最新发布 →（本地快照或在线清单，均需 Ed25519 验签）→
/// 本地扫描与分批 check → 下载源探测 → 工单下载（密文校验后落定）→ 提交快照与陈旧清理。
/// 只填充 <c>&lt;gameDir&gt;/qlrpu/cache</c> 的加密对象，绝不解密落盘；明文的完整认证与
/// 校验由游戏内 Mod 在挂载资源包时完成。同一缓存同时只允许一个更新方（update.lock）。
/// </summary>
public sealed class RpuSyncEngine : IDisposable
{
    /// <summary>check / 下载工单的单批对象数（与 Java 端一致）。</summary>
    private const int BatchSize = 128;

    /// <summary>单个对象的下载尝试次数（失败后换新工单重来）。</summary>
    private const int DownloadAttempts = 3;

    private readonly RpuCache _cache;
    private readonly RpuClient _client;
    private readonly HttpClient _downloadHttp;
    private readonly byte[] _signingKey;

    private long _lastReportTick;
    private RpuSyncStage _lastStage = (RpuSyncStage)(-1);

    /// <summary>进度回调（100ms 节流；阶段切换与阶段收尾必定上报）。</summary>
    public event Action<RpuSyncProgress>? ProgressChanged;

    /// <summary>以部署配置缺省值构造。</summary>
    public RpuSyncEngine(string gameDir) : this(gameDir, RpuDeploymentConfig.Load()) { }

    /// <summary>以指定部署配置构造（测试注入用）。</summary>
    public RpuSyncEngine(string gameDir, (string ApiBase, byte[] SigningKey) deployment)
    {
        _cache = new RpuCache(gameDir);
        _client = new RpuClient(deployment.ApiBase);
        _signingKey = deployment.SigningKey;
        _downloadHttp = new HttpClient(new SocketsHttpHandler
        {
            // 加密对象是不透明二进制：绝不能被自动解压干扰密文校验。
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        })
        {
            Timeout = TimeSpan.FromMinutes(10),
            DefaultRequestHeaders = { { "User-Agent", RpuClient.UserAgent } },
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _client.Dispose();
        _downloadHttp.Dispose();
        _cache.Dispose();
    }

    // ────────────────────────────── 主流程 ──────────────────────────────

    /// <summary>执行一次缓存预热同步；取消时返回 Canceled 总账，业务失败抛 RpuException。</summary>
    public async Task<RpuSyncResult> SyncAsync(CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        string? releaseId = null;
        int filesTotal = 0;
        long totalBytes = 0;
        int validFiles = 0;
        int downloaded = 0;
        int cleaned = 0;
        bool nothingToDownload = false;
        try
        {
            Report(RpuSyncStage.Initializing, "准备缓存目录", 0, 1, 0, 0, force: true);
            _cache.AcquireUpdateLock();
            Globals.Log("RPU 同步开始：" + _cache.Root);

            Report(RpuSyncStage.Authorizing, "连接更新服务…", 0, 1, 0, 0, force: true);
            var latest = await _client.GetLatestAsync(ct);
            releaseId = latest.ReleaseId;

            var (manifest, rawEnvelope, fromLocal) = await LoadManifestAsync(latest.ReleaseId, ct);
            Globals.Log($"RPU 发布 {latest.ReleaseId}（{(fromLocal ? "本地快照" : "在线验签")}，{manifest.Packs.Count} 个资源包）。");

            var files = new List<(string Pack, RpuFileEntry File)>();
            foreach (var pack in manifest.Packs)
            foreach (var file in pack.Files)
                files.Add((pack.Name ?? pack.Id, file));
            filesTotal = files.Count;
            totalBytes = files.Sum(x => x.File.CipherSize);

            var (required, valid) = await ScanAndCheckAsync(latest.ReleaseId, files, totalBytes, ct);
            validFiles = valid;
            nothingToDownload = required.Count == 0;

            if (!nothingToDownload)
            {
                var (chosen, usable) = await ProbeSourcesAsync(latest.ReleaseId, ct);
                Globals.Log($"RPU 下载源选定：{chosen.Name}（探测可用 {usable} 个）。");
                downloaded = await DownloadRequiredAsync(latest.ReleaseId, chosen, files, required, ct);
            }
            else
            {
                Globals.Log("没有资源包更新，无需下载文件；继续在线授权与完整校验。");
            }

            _cache.CommitCurrent(rawEnvelope);
            cleaned = _cache.Cleanup(CipherShaSet(manifest));
            if (cleaned > 0)
                Globals.Log($"RPU 缓存清理：{cleaned} 个陈旧对象已删除（保留期 {RpuCache.RetentionDays} 天）。");

            Report(RpuSyncStage.Ready, "校验完成", totalBytes, totalBytes, filesTotal, filesTotal, force: true);
            watch.Stop();
            Globals.Log($"RPU 同步完成：下载 {downloaded}、本地复用 {validFiles}、清理 {cleaned}，用时 {watch.Elapsed.TotalSeconds:0.0}s。");
            return new RpuSyncResult(releaseId, filesTotal, validFiles, downloaded, cleaned, nothingToDownload, false);
        }
        catch (OperationCanceledException)
        {
            Globals.Log("RPU 同步已取消。");
            return new RpuSyncResult(releaseId, filesTotal, validFiles, downloaded, cleaned, nothingToDownload, true);
        }
        finally
        {
            _cache.ReleaseLock();
        }
    }

    /// <summary>装载发布清单：本地快照命中且验签通过则复用，否则在线获取并验签。</summary>
    private async Task<(RpuManifest Manifest, string RawEnvelope, bool FromLocal)> LoadManifestAsync(
        string releaseId, CancellationToken ct)
    {
        var localRelease = _cache.ReadCurrentReleaseId();
        if (localRelease is not null && localRelease.Equals(releaseId, StringComparison.Ordinal))
        {
            var raw = _cache.ReadCurrentEnvelopeRaw();
            if (raw is not null)
            {
                try
                {
                    var envelope = JsonSerializer.Deserialize<RpuEnvelope>(raw);
                    var manifest = envelope is null
                        ? null : RpuProtocol.VerifyAndParseManifest(envelope, releaseId, _signingKey);
                    if (manifest is not null)
                        return (manifest, raw, true);
                }
                catch (Exception e) when (e is RpuException or JsonException or FormatException)
                {
                    Globals.Log("RPU 本地快照校验失败，回退在线获取：" + e.Message);
                }
            }
        }
        var (fetchedEnvelope, fetchedRaw) = await _client.GetEnvelopeAsync(releaseId, ct);
        var fetched = RpuProtocol.VerifyAndParseManifest(fetchedEnvelope, releaseId, _signingKey);
        return (fetched, fetchedRaw, false);
    }

    /// <summary>
    /// 扫描本地缓存并分批 check（128/批，无效对象上报空 cipherSha256）。
    /// 服务端每批返回的 required 必须是该批对象的子集，否则视作协议异常。
    /// </summary>
    private async Task<(IReadOnlySet<string> Required, int ValidFiles)> ScanAndCheckAsync(
        string releaseId, List<(string Pack, RpuFileEntry File)> files, long totalBytes, CancellationToken ct)
    {
        Report(RpuSyncStage.Checking, $"扫描 {files.Count} 个文件…", 0, totalBytes, 0, files.Count, force: true);
        var required = new HashSet<string>(StringComparer.Ordinal);
        var batch = new List<(string ObjectId, bool Valid, string CipherSha256)>(BatchSize);
        long done = 0;
        int valid = 0;
        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = files[i].File;
            bool isValid = _cache.IsObjectValid(entry);
            if (isValid)
            {
                valid++;
                done += entry.CipherSize;
            }
            batch.Add((entry.ObjectId, isValid, isValid ? entry.CipherSha256 : ""));
            if (batch.Count == BatchSize || i == files.Count - 1)
            {
                var batchIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var b in batch) batchIds.Add(b.ObjectId);
                var req = await _client.CheckAsync(releaseId, batch, ct);
                foreach (var id in req)
                {
                    if (!batchIds.Contains(id))
                        throw new RpuException("服务端返回了发布之外的缓存对象。");
                    required.Add(id);
                }
                batch.Clear();
            }
            Report(RpuSyncStage.Checking, $"校验本地对象 {i + 1}/{files.Count}", done, totalBytes, i + 1, files.Count);
        }
        Globals.Log($"RPU 扫描完成：{files.Count} 个文件，{required.Count} 个对象需要下载。");
        return (required, valid);
    }

    /// <summary>获取下载源并探测可用性（probeUrl 轻量 GET）；全部不可用则抛出。</summary>
    private async Task<(RpuSource Chosen, int UsableCount)> ProbeSourcesAsync(string releaseId, CancellationToken ct)
    {
        Report(RpuSyncStage.Sources, "探测下载源…", 0, 0, 0, 0, force: true);
        var sources = await _client.GetSourcesAsync(releaseId, ct);
        var results = await Task.WhenAll(sources.Select(async s =>
        {
            bool ok = await ProbeSourceAsync(s, ct);
            Globals.Log($"RPU 下载源探测：{s.Name} —— {(ok ? "可用" : "不可用")}。");
            return (Source: s, Ok: ok);
        }));
        var usable = results.Where(r => r.Ok).Select(r => r.Source).ToList();
        if (usable.Count == 0)
            throw new RpuException("当前发布没有可用下载源，请重试或跳过。");
        Report(RpuSyncStage.Sources, $"选定 {usable[0].Name}（可用 {usable.Count} 个）", 0, 0, 0, 0, force: true);
        return (usable[0], usable.Count);
    }

    /// <summary>单个下载源探测：GET probeUrl 读取至多 64KB（8 秒超时）。</summary>
    private async Task<bool> ProbeSourceAsync(RpuSource source, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(source.ProbeUrl))
            return true; // 服务端已声明可用
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var resp = await _downloadHttp.GetAsync(source.ProbeUrl,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!resp.IsSuccessStatusCode) return false;
            await using var stream = await resp.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[4096];
            long read = 0;
            int n;
            while (read < 64 * 1024 && (n = await stream.ReadAsync(buffer, timeout.Token)) > 0)
                read += n;
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 探测超时，不算用户取消
        }
        catch (Exception e) when (e is IOException or HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>下载全部缺失对象（按 128 批取工单；同一对象只下载一次）。</summary>
    private async Task<int> DownloadRequiredAsync(string releaseId, RpuSource source,
        List<(string Pack, RpuFileEntry File)> files, IReadOnlySet<string> required, CancellationToken ct)
    {
        var requiredFiles = files.Where(x => required.Contains(x.File.ObjectId))
            .GroupBy(x => x.File.ObjectId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        long totalBytes = requiredFiles.Sum(x => x.File.CipherSize);
        Report(RpuSyncStage.Downloading, $"需要下载 {requiredFiles.Count} 个对象", 0, totalBytes, 0, requiredFiles.Count, force: true);

        long done = 0;
        int completed = 0;
        var tickets = new Dictionary<string, RpuTicket>(StringComparer.Ordinal);
        for (int offset = 0; offset < requiredFiles.Count; offset += BatchSize)
        {
            var chunk = requiredFiles.Skip(offset).Take(BatchSize).ToList();
            var ids = chunk.Select(x => x.File.ObjectId).ToList();
            foreach (var ticket in await _client.GetTicketsAsync(releaseId, source.Id, ids, ct))
                tickets[ticket.ObjectId] = ticket;

            foreach (var (_, file) in chunk)
            {
                ct.ThrowIfCancellationRequested();
                Report(RpuSyncStage.Downloading, $"下载 {file.Path}", done, totalBytes, completed, requiredFiles.Count);
                await DownloadObjectAsync(releaseId, source.Id, file, tickets, ct,
                    (bytes, _) => Report(RpuSyncStage.Downloading, $"下载 {file.Path}",
                        done + bytes, totalBytes, completed, requiredFiles.Count));
                done += file.CipherSize;
                completed++;
                Report(RpuSyncStage.Downloading, $"已下载 {completed}/{requiredFiles.Count}",
                    done, totalBytes, completed, requiredFiles.Count, force: true);
            }
        }
        return completed;
    }

    /// <summary>下载并落定单个对象：流式写入 .part，尝试失败换新工单重来，耗尽抛 RpuException。</summary>
    private async Task DownloadObjectAsync(string releaseId, string sourceId, RpuFileEntry file,
        Dictionary<string, RpuTicket> tickets, CancellationToken ct, Action<long, long>? onProgress)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!tickets.TryGetValue(file.ObjectId, out var ticket) || !RpuClient.TicketUsable(ticket))
                {
                    var fresh = await _client.GetTicketsAsync(releaseId, sourceId, new[] { file.ObjectId }, ct);
                    ticket = fresh[0];
                    tickets[file.ObjectId] = ticket;
                }
                await DownloadToPartAsync(file, ticket.Url, ct, onProgress);
                _cache.CompleteDownload(file);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // 403/尺寸异常等可能是工单或来源对象出了问题：丢掉缓存的工单，下次尝试换新的。
                tickets.Remove(file.ObjectId);
                if (attempt >= DownloadAttempts)
                    throw new RpuException($"下载对象失败（已尝试 {attempt} 次）：{DescribeDownloadFailure(e)}");
                var delayMs = 1000 * attempt;
                Globals.Log($"RPU 下载第 {attempt} 次尝试失败（{file.ObjectId[..8]}…）：{DescribeDownloadFailure(e)}，{delayMs}ms 后重试。");
                await Task.Delay(delayMs, ct);
            }
        }
    }

    /// <summary>把对象密文流式写入 .part（不落定；校验与原子替换交给 CompleteDownload）。</summary>
    private async Task DownloadToPartAsync(RpuFileEntry file, string url, CancellationToken ct,
        Action<long, long>? onProgress)
    {
        using var resp = await _downloadHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if ((int)resp.StatusCode != 200)
            throw new RpuException((int)resp.StatusCode switch
            {
                403 => "下载工单已失效，请重试。",
                404 => "下载对象暂不可用，请重试。",
                >= 500 => "下载服务暂时不可用，请稍后重试。",
                _ => $"下载服务异常（HTTP {(int)resp.StatusCode}）。",
            });
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        if (resp.Content.Headers.ContentLength is { } len && len != file.CipherSize)
            throw new RpuException("下载对象尺寸与服务端清单不符。");

        using var part = _cache.OpenPartWrite(file.CipherSha256);
        var buffer = new byte[256 * 1024];
        long received = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await part.WriteAsync(buffer.AsMemory(0, n), ct);
            received += n;
            onProgress?.Invoke(received, file.CipherSize);
        }
        if (received != file.CipherSize)
            throw new RpuException("连接提前结束，文件尚未接收完整");
        await part.FlushAsync(ct);
    }

    /// <summary>下载失败的简中原因（与 qlrpu-client DownloadRetry.reason 的口径一致）。</summary>
    private static string DescribeDownloadFailure(Exception e) => e switch
    {
        RpuException r => r.Message,
        TaskCanceledException => "网络读取超时",
        HttpRequestException h when h.InnerException is SocketException => "无法连接下载服务，请确认下载源可用",
        HttpRequestException => "网络连接中断",
        IOException => "连接提前结束，文件尚未接收完整",
        _ => "网络连接中断",
    };

    /// <summary>当前发布引用的全部密文哈希（清理保留集）。</summary>
    private static IReadOnlySet<string> CipherShaSet(RpuManifest manifest)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in manifest.Packs)
        foreach (var file in pack.Files)
            set.Add(file.CipherSha256);
        return set;
    }

    private void Report(RpuSyncStage stage, string detail, long done, long total, int filesDone, int filesTotal,
        bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && stage == _lastStage && now - _lastReportTick < 100) return;
        _lastReportTick = now;
        _lastStage = stage;
        ProgressChanged?.Invoke(new RpuSyncProgress(stage, RpuSyncStageText.Label(stage), detail, done, total, filesDone, filesTotal));
    }
}
