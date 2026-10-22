using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public enum SyncState
{
    Idle,
    Discovering,
    Fetching,
    Writing,
    Deduping,
    Done,
    Partial,
    Error
}

public class SyncProgressReport
{
    public SyncState State { get; set; }
    public string ServerId { get; set; } = string.Empty;
    public string ServerName { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public int CurrentItem { get; set; }
    public int TotalItems { get; set; }
    public int ItemsFetched { get; set; }
    public int ItemsWritten { get; set; }
    public double Percentage { get; set; }
    public double ServerPercentage { get; set; }
    public int BatchSize { get; set; }
    public long LatencyMs { get; set; }
    public int ActiveServersCount { get; set; }
    public int TotalServersCount { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

public class SyncBenchmarkMetrics
{
    public long ServerRequestMs { get; set; }
    public long DetailEnrichmentMs { get; set; } // 0ms (N+1 eliminated by Fields= query)
    public long ImageDownloadMs { get; set; }    // 0ms (lazy on-screen rendering)
    public long SqliteWriteMs { get; set; }
    public long TotalElapsedMs { get; set; }
    public int ItemsProcessed { get; set; }

    public override string ToString() =>
        $"[sync:benchmark] {ItemsProcessed:N0} items in {TotalElapsedMs:N0}ms: " +
        $"Server Request: {ServerRequestMs:N0}ms ({(TotalElapsedMs > 0 ? (ServerRequestMs * 100.0 / TotalElapsedMs) : 0):F1}%), " +
        $"Detail Fetches: {DetailEnrichmentMs:N0}ms (0% — N+1 eliminated by Fields=), " +
        $"Image Downloads: {ImageDownloadMs:N0}ms (0% — lazy on-screen), " +
        $"SQLite Writes: {SqliteWriteMs:N0}ms ({(TotalElapsedMs > 0 ? (SqliteWriteMs * 100.0 / TotalElapsedMs) : 0):F1}%)";
}

public class SyncBatch
{
    public ServerConfig Server { get; init; } = null!;
    public EmbyItem View { get; init; } = null!;
    public int StartIndex { get; init; }
    public int NextStartIndex { get; init; } // the page's end in the library's paging (Items can be fewer: Series rows dropped)
    public int TotalInView { get; init; }
    public List<EmbyItem> Items { get; init; } = new();
    public bool IsCompleted { get; init; }
    public long FetchDurationMs { get; init; }
    public bool IsSeriesPrefetch { get; init; } // written ahead of the library's paging; no checkpoint
    public bool IsIncremental { get; init; }     // changes since the last pass; checkpointed only when complete
    public int LibraryTotal { get; init; }       // the library's item count (the checkpoint an incremental pass writes)
}

public class SyncEngine
{
    private readonly EmbyClient _embyClient;
    private readonly DatabaseService _db;
    private readonly DedupEngine _dedup;
    private readonly ConcurrentDedupIndex _dedupIndex = new();
    private readonly TraktService _trakt;
    private readonly SettingsService _settings;

    public event Action<SyncState, string>? StateChanged;
    public event Action<int, int>? ProgressChanged;
    public event Action<SyncProgressReport>? ProgressReported;
    public event Action? SyncCompleted;
    public event Action<string>? DedupAlertRaised; // FeatureFlags.DedupAlert after a sync's gate measurement

    private readonly Func<ServerConfig, EmbyClient> _clientFor;

    // An incremental pass asks for what was saved since the last completed pass minus this much: it covers items
    // saved while that pass ran and clock skew between this machine and the server, at the cost of a day's changes.
    public static readonly TimeSpan IncrementalOverlap = TimeSpan.FromHours(24);

    public SyncEngine(EmbyClient embyClient, DatabaseService db, DedupEngine dedup, TraktService trakt, SettingsService settings,
        Func<ServerConfig, EmbyClient>? clientFor = null)
    {
        _embyClient = embyClient;
        _db = db;
        _dedup = dedup;
        _trakt = trakt;
        _settings = settings;
        _clientFor = clientFor ?? EmbyClient.CreateForServer;
    }

    public SyncState CurrentState { get; private set; } = SyncState.Idle;

    public void SetServerPriority(IList<string> priorityList)
    {
        _dedupIndex.SetServerPriority(priorityList);
    }

    public Task SyncAllAsync(CancellationToken ct = default, bool forceResync = false) => RunFullSyncAsync(ct, forceResync);

    // Syncs queue behind each other and behind a dedup rebuild (DedupEngine.WriterGate): two overlapping runs
    // shared one index, and the second's InitializeFromDb replaced it under the first's writer.
    public async Task RunFullSyncAsync(CancellationToken ct = default, bool forceResync = false)
    {
        if (!await _dedup.WriterGate.WaitAsync(0, ct))
        {
            NocturneLogger.Info("SyncEngine", "Sync queued behind a running sync or dedup rebuild.");
            await _dedup.WriterGate.WaitAsync(ct);
        }
        try
        {
            var interrupted = _db.GetMigrationState(SyncRunningKey) == "1";
            _db.SetMigrationState(SyncRunningKey, "1"); // cleared below once the run completes; still set, the next run was interrupted
            _resumedFromCheckpoint = false;
            await RunFullSyncCoreAsync(ct, forceResync);
            if (!ct.IsCancellationRequested)
            {
                var status = _settings.Current.SyncStatus;
                if (status != "failed" && DeletionSweepDue()) await DeletionSweepCoreAsync(ct);
                if (DriftReason(interrupted, status) is { } why)
                {
                    NocturneLogger.Info("SyncEngine", $"Measuring drift: {why}.");
                    await Task.Run(() => DedupEngine.MeasureDriftCore(_db, _settings.Current.PreferredServerPriority, _settings.Current.Servers.Count), CancellationToken.None);
                }
                _db.SetMigrationState(SyncRunningKey, "0");
                await Task.Run(() => FeatureFlags.MeasureCascadeGate(_db), CancellationToken.None);
                if (FeatureFlags.DedupAlert(_db) is { } alert) DedupAlertRaised?.Invoke(alert);
            }
        }
        finally
        {
            _dedup.WriterGate.Release();
        }
    }

    // #22 item 4: drift (DedupEngine.MeasureDrift: 190 s, 4 GB on the live library) only accrues from a sync that did
    // not complete cleanly, since a clean pass groups like a rebuild (#20 item 2). Measured after a partial or failed
    // sync, after one that resumed from a checkpoint or follows an interrupted run, monthly as a backstop (its clock
    // starts at the first sync), and from Settings.
    public const string SyncRunningKey = "sync_running";
    public const string DriftClockKey = "dedup_drift_clock_utc";
    public static readonly TimeSpan DriftBackstop = TimeSpan.FromDays(30);
    private bool _resumedFromCheckpoint;

    private string? DriftReason(bool interrupted, string? status)
    {
        if (interrupted) return "the previous sync did not finish";
        if (status is "partial" or "failed") return $"this sync was {status}";
        if (_resumedFromCheckpoint) return "this sync resumed from a checkpoint";
        var last = _db.GetMigrationState(DedupEngine.DriftMeasuredKey) ?? _db.GetMigrationState(DriftClockKey);
        if (last == null)
        {
            _db.SetMigrationState(DriftClockKey, DateTime.UtcNow.ToString("o"));
            return null;
        }
        return DateTime.TryParse(last, null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && DateTime.UtcNow - at >= DriftBackstop
            ? "monthly backstop" : null;
    }

    // The deletion sweep (#21 item 1). An incremental pass asks what was saved since the last one, and a deleted item is
    // never saved again, so its row would stay forever. Weekly after a sync, and from Settings.
    public const string DeletionSweepKey = "deletion_sweep_utc";
    public static readonly TimeSpan DeletionSweepInterval = TimeSpan.FromDays(7);
    // More of a library missing than this reads as an unmounted share or a server fault, not deletions: left alone.
    public const double DeletionSweepMaxShare = 0.2;

    public record DeletionSweepResult(int Libraries, int Removed, int Skipped);

    private bool DeletionSweepDue() =>
        !DateTime.TryParse(_db.GetMigrationState(DeletionSweepKey), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last)
        || DateTime.UtcNow - last >= DeletionSweepInterval;

    public async Task<DeletionSweepResult> RunDeletionSweepAsync(CancellationToken ct = default)
    {
        await _dedup.WriterGate.WaitAsync(ct);
        try
        {
            var result = await DeletionSweepCoreAsync(ct);
            if (result.Removed > 0) await Task.Run(() => FeatureFlags.MeasureCascadeGate(_db), CancellationToken.None);
            return result;
        }
        finally
        {
            _dedup.WriterGate.Release();
        }
    }

    // Per library: list every id (minimal fields), diff against the cache, ask the server by id about each one missing
    // (an item added or removed mid-listing shifts the paging), and remove what it confirms gone. A library whose
    // listing fails, or with more than DeletionSweepMaxShare of it missing, is skipped.
    private async Task<DeletionSweepResult> DeletionSweepCoreAsync(CancellationToken ct)
    {
        int libraries = 0, removed = 0, skipped = 0;
        var priority = _settings.Current.PreferredServerPriority;
        foreach (var server in _settings.Current.Servers)
        {
            var client = _clientFor(server);
            foreach (var view in await client.GetViewsAsync())
            {
                if (ct.IsCancellationRequested) return new(libraries, removed, skipped);
                var cached = _db.GetLibraryItemIds(server.Id, view.Id);
                if (cached.Count == 0) continue;
                var clock = Stopwatch.StartNew();
                var listed = await client.GetLibraryItemIdsAsync(view.Id, ct: ct);
                libraries++;
                if (listed == null)
                {
                    skipped++;
                    continue;
                }
                var missing = cached.Where(id => !listed.Contains(id)).ToList();
                if (missing.Count > cached.Count * DeletionSweepMaxShare)
                {
                    NocturneLogger.Warn("SyncEngine", $"Deletion sweep: {server.Name}/{view.Name} lists {missing.Count:N0} of {cached.Count:N0} cached items as missing; left alone.");
                    skipped++;
                    continue;
                }
                if (missing.Count > 0)
                {
                    if (await client.GetExistingItemIdsAsync(missing, ct) is not { } stillThere)
                    {
                        skipped++;
                        continue;
                    }
                    missing.RemoveAll(stillThere.Contains);
                    removed += _db.RemoveDeletedItems(server.Id, missing, priority);
                }
                NocturneLogger.Info("SyncEngine", $"Deletion sweep: {server.Name}/{view.Name}: {listed.Count:N0} ids listed in {clock.ElapsedMilliseconds:N0} ms, {missing.Count:N0} removed.");
            }
        }
        _db.SetMigrationState(DeletionSweepKey, DateTime.UtcNow.ToString("o"));
        return new(libraries, removed, skipped);
    }

    // A library's Series rows, paged and written before the library itself (not checkpointed: a resumed sync
    // fetches them again). False on a request that fails after retries.
    private static async Task<bool> SendSeriesFirstAsync(EmbyClient client, ServerConfig server, EmbyItem view, DateTime? since, ChannelWriter<SyncBatch> writer, CancellationToken ct)
    {
        const int pageSize = 2000;
        for (int start = 0; !ct.IsCancellationRequested; start += pageSize)
        {
            var page = await client.GetItemsAsync(parentId: view.Id, includeItemTypes: "Series", limit: pageSize, startIndex: start, fields: EmbyClient.FullSyncItemFields, minDateLastSavedForUser: since);
            if (!page.IsSuccess) return false;
            foreach (var item in page.Items)
            {
                item.ServerId = server.Id;
                item.ServerName = server.Name;
            }
            if (page.Items.Count > 0)
                await writer.WriteAsync(new SyncBatch { Server = server, View = view, Items = page.Items, IsSeriesPrefetch = true }, ct);
            if (page.Items.Count < pageSize) return true;
        }
        return true;
    }

    private async Task RunFullSyncCoreAsync(CancellationToken ct, bool forceResync)
    {
        var servers = _settings.Current.Servers.ToList();
        if (servers.Count == 0)
        {
            if (_embyClient.IsAuthenticated)
            {
                servers.Add(new ServerConfig
                {
                    Id = _settings.Current.ActiveServerId ?? "primary",
                    Name = "Emby Server",
                    Url = _embyClient.BaseUrl,
                    AccessToken = _embyClient.Token,
                    UserId = _embyClient.UserId
                });
            }
            else
            {
                return;
            }
        }

        try
        {
            // =========================================================================
            // PHASE 0: DISCOVERY & PROBING (Parallel across servers, sequential per server)
            // =========================================================================
            var discMsg = $"Discovering libraries across {servers.Count} server(s)...";
            SetState(SyncState.Discovering, discMsg);
            ProgressReported?.Invoke(new SyncProgressReport
            {
                State = SyncState.Discovering,
                StatusMessage = discMsg,
                Percentage = 5.0,
                TotalServersCount = servers.Count,
                ActiveServersCount = servers.Count
            });

            // Concurrently query views from each distinct server
            var discResults = await Task.WhenAll(servers.Select(async server =>
            {
                ProgressReported?.Invoke(new SyncProgressReport
                {
                    State = SyncState.Discovering,
                    ServerId = server.Id,
                    ServerName = server.Name,
                    StatusMessage = "Connecting & discovering libraries...",
                    Percentage = 5.0,
                    ServerPercentage = 0,
                    TotalServersCount = servers.Count,
                    ActiveServersCount = servers.Count
                });

                var client = _clientFor(server);
                try
                {
                    var views = await client.GetViewsAsync();
                    return (Server: server, Views: views, Success: true);
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("SyncEngine", $"Failed to fetch views from server {server.Name}: {ex.Message}");
                    return (Server: server, Views: new List<EmbyItem>(), Success: false);
                }
            }));

            // GetViewsAsync returns an empty list on any failure, so "no libraries" is treated as unreachable: the
            // server used to be dropped here and the run still reported "completed successfully".
            var discoveredServerViews = new List<(ServerConfig Server, List<EmbyItem> Views)>();
            var unreachableServers = new List<ServerConfig>();
            foreach (var res in discResults)
            {
                if (res.Success && res.Views.Count > 0)
                {
                    discoveredServerViews.Add((res.Server, res.Views));
                }
                else
                {
                    unreachableServers.Add(res.Server);
                    NocturneLogger.Warn("SyncEngine", $"{res.Server.Name}: no libraries returned (unreachable, or none visible to this user); not synced this run.");
                }
            }

            // Probe counts: parallel across servers, but sequential per server to protect server load
            var probeResults = await Task.WhenAll(discoveredServerViews.Select(async entry =>
            {
                var server = entry.Server;
                var views = entry.Views;
                var client = _clientFor(server);

                var list = new List<(ServerConfig Server, EmbyItem View, int ServerTotal, DateTime? Since, int StartIndex)>();

                foreach (var view in views)
                {
                    if (ct.IsCancellationRequested) break;
                    var checkpoint = _db.GetSyncCheckpoint(server.Id, view.Id);
                    var probe = await client.GetItemsAsync(parentId: view.Id, limit: 1);
                    int serverTotal = probe.IsSuccess ? probe.TotalRecordCount : (checkpoint?.TotalItems ?? 0);

                    // A completed library is fetched incrementally: what was saved since its last pass (#20 item 5). It
                    // used to be skipped when its item count matched the checkpoint and fetched in full otherwise, so a
                    // changed item went unseen and one new episode re-fetched 676,870.
                    DateTime? since = null;
                    int startIndex = 0;

                    if (!forceResync && checkpoint != null && checkpoint.IsCompleted)
                    {
                        since = DateTimeOffset.FromUnixTimeSeconds(checkpoint.LastSynced).UtcDateTime - IncrementalOverlap;
                    }
                    else if (!forceResync && checkpoint != null && !checkpoint.IsCompleted && checkpoint.StartIndex > 0)
                    {
                        startIndex = checkpoint.StartIndex;
                        _resumedFromCheckpoint = true;
                    }

                    list.Add((server, view, serverTotal, since, startIndex));
                }

                return list;
            }));

            var probedViews = probeResults.SelectMany(x => x).ToList();
            int totalViews = probedViews.Count;
            int totalExpectedItems = probedViews.Sum(x => x.ServerTotal);
            int totalProcessedItems = probedViews.Where(x => x.Since != null).Sum(x => x.ServerTotal) +
                                      probedViews.Where(x => x.Since == null).Sum(x => x.StartIndex);

            // =========================================================================
            // PHASE 1: PIPELINE — PARALLELIZE NETWORK, SERIALIZE WRITES (Bounded Channel)
            // =========================================================================
            // Channel capacity = 8 batches (~10,000 items max in flight). Backpressure bounds memory to <20 MB.
            var channel = Channel.CreateBounded<SyncBatch>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false, // Multiple parallel server fetchers write to the channel
                SingleReader = true   // Single SQLite writer task
            });

            var viewWatermarks = new ConcurrentDictionary<string, int>();
            var serverTotalItems = new ConcurrentDictionary<string, int>();
            var serverWrittenItems = new ConcurrentDictionary<string, int>();
            var serverFetchedItems = new ConcurrentDictionary<string, int>();

            foreach (var group in probedViews.GroupBy(p => p.Server.Id))
            {
                int sTotal = group.Sum(p => p.ServerTotal);
                int sInitial = group.Sum(p => p.Since != null ? p.ServerTotal : p.StartIndex);
                serverTotalItems[group.Key] = sTotal;
                serverWrittenItems[group.Key] = sInitial;
                serverFetchedItems[group.Key] = sInitial;
            }

            int totalItemsFetched = totalProcessedItems;
            int totalItemsWritten = totalProcessedItems;
            long totalServerRequestMs = 0;
            long totalSqliteWriteMs = 0;
            var failedServers = new ConcurrentDictionary<string, byte>();
            foreach (var srv in unreachableServers) failedServers[srv.Id] = 1;

            var syncSw = Stopwatch.StartNew();

            _settings.Update(s => s.SyncStatus = "in-progress");
            _dedupIndex.SetServerPriority(_settings.Current.PreferredServerPriority);
            _dedupIndex.SetConnectedServerCount(servers.Count);
            _dedupIndex.InitializeFromDb(_db);

            // ── Single Consumer / Writer Task (Serialized SQLite Transactions) ──
            var writerTask = Task.Run(async () =>
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var batch))
                    {
                        var swWrite = Stopwatch.StartNew();

                        // Concurrent incremental hash-index deduplication:
                        // Matches items across servers in real time and assigns DedupGroupId prior to DB write
                        _dedupIndex.ProcessBatch(batch.Items, _db, ConcurrentDedupIndex.UntaggedEpisodes.Hold);

                        if (batch.IsSeriesPrefetch || (batch.IsIncremental && !batch.IsCompleted))
                        {
                            _db.UpsertItems(batch.Items, batch.Server.Id, batch.View.Id);
                        }
                        else if (batch.IsIncremental)
                        {
                            // The pass is done: the library checkpoint stays complete, and its time is the next "since".
                            _db.UpsertItems(batch.Items, batch.Server.Id, batch.View.Id,
                                checkpointStartIndex: batch.LibraryTotal, checkpointTotalItems: batch.LibraryTotal, checkpointIsCompleted: true);
                        }
                        else
                        {
                            string watermarkKey = $"{batch.Server.Id}:{batch.View.Id}";
                            int watermark = batch.NextStartIndex;
                            viewWatermarks[watermarkKey] = watermark;

                            // Atomic crash safety: items + checkpoint committed in the exact same transaction!
                            _db.UpsertItems(
                                batch.Items,
                                batch.Server.Id,
                                batch.View.Id,
                                checkpointStartIndex: watermark,
                                checkpointTotalItems: batch.TotalInView,
                                checkpointIsCompleted: batch.IsCompleted);
                        }

                        swWrite.Stop();
                        Interlocked.Add(ref totalSqliteWriteMs, swWrite.ElapsedMilliseconds);
                        Interlocked.Add(ref totalItemsWritten, batch.Items.Count);

                        int sWritten = serverWrittenItems.AddOrUpdate(batch.Server.Id, batch.Items.Count, (_, val) => val + batch.Items.Count);
                        int sTotal = serverTotalItems.TryGetValue(batch.Server.Id, out var st) ? st : 0;
                        double sPercent = sTotal > 0 ? Math.Min(100.0, ((double)sWritten / sTotal) * 100.0) : 100.0;

                        double overallPercent = totalExpectedItems > 0
                            ? Math.Min(90.0, 10.0 + (((double)Math.Min(totalItemsFetched, totalItemsWritten) / totalExpectedItems) * 78.0))
                            : 10.0;

                        var msg = batch.IsSeriesPrefetch ? $"{batch.View.Name}: series" : $"{batch.View.Name}: {batch.NextStartIndex:N0} / {batch.TotalInView:N0} items";
                        ProgressReported?.Invoke(new SyncProgressReport
                        {
                            State = SyncState.Writing,
                            ServerId = batch.Server.Id,
                            ServerName = batch.Server.Name,
                            LibraryName = batch.View.Name,
                            CurrentItem = batch.NextStartIndex,
                            TotalItems = batch.TotalInView,
                            ItemsFetched = totalItemsFetched,
                            ItemsWritten = totalItemsWritten,
                            Percentage = overallPercent,
                            ServerPercentage = sPercent,
                            BatchSize = batch.Items.Count,
                            LatencyMs = batch.FetchDurationMs,
                            TotalServersCount = servers.Count,
                            ActiveServersCount = Math.Max(1, servers.Count - failedServers.Count),
                            StatusMessage = msg
                        });
                    }
                }
            }, ct);

            // ── Parallel Multi-Server Producer with Adaptive Latency Batch Sizing ──
            // Group libraries by server: distinct servers fetch concurrently (up to maxConcurrentServers),
            // while queries within each server execute sequentially (1 active query per server) to avoid server load.
            var viewsByServer = probedViews
                .GroupBy(p => p.Server.Id)
                .Select(g => (Server: g.First().Server, Views: g.ToList()))
                .ToList();

            const int maxConcurrentServers = 4;
            using var serverSemaphore = new SemaphoreSlim(maxConcurrentServers);
            int completedViews = 0;

            SetState(SyncState.Fetching, servers.Count == 1 ? $"Syncing {servers[0].Name}..." : $"Syncing {servers.Count} servers in parallel...");

            var serverTasks = viewsByServer.Select(async group =>
            {
                await serverSemaphore.WaitAsync(ct);
                try
                {
                    var server = group.Server;
                    var client = _clientFor(server);

                    var batchController = new AdaptiveBatchController(AdaptiveBatchController.DefaultBatchSize);

                    foreach (var (_, view, serverTotal, since, initialStartIndex) in group.Views)
                    {
                        if (ct.IsCancellationRequested) break;

                        int doneViews = Interlocked.Increment(ref completedViews);
                        ProgressChanged?.Invoke(doneViews, Math.Max(1, totalViews));

                        if (since != null)
                            NocturneLogger.Info("SyncEngine", $"{server.Name} • {view.Name}: fetching what changed since {since:u} ({serverTotal:N0} items in the library).");

                        int startIndex = initialStartIndex;
                        if (startIndex > 0)
                        {
                            NocturneLogger.Info("SyncEngine", $"Resuming sync for {server.Name} • {view.Name} from item {startIndex:N0} / {serverTotal:N0} (batch size {batchController.CurrentBatchSize})...");
                        }

                        // Series rows first: an episode deduplicated before its Series row is cached has no series
                        // provider ids (no ep:tvdb/tmdb/imdb keys) and groups differently from a rebuild of the same
                        // items. In SortName order half the live library's episodes came before their series (#20).
                        if (!await SendSeriesFirstAsync(client, server, view, since, channel.Writer, ct))
                        {
                            NocturneLogger.Error("SyncEngine", $"Network error fetching the series of {server.Name} • {view.Name}; library skipped this run.");
                            failedServers[server.Id] = 1;
                            break;
                        }

                        bool hasMore = true;
                        var libSw = Stopwatch.StartNew();
                        long libServerRequestMs = 0;
                        int libItemsFetched = 0;

                        while (hasMore && !ct.IsCancellationRequested)
                        {
                            int currentLimit = batchController.CurrentBatchSize;
                            var swReq = Stopwatch.StartNew();
                            var itemsResult = await client.GetItemsAsync(
                                parentId: view.Id,
                                limit: currentLimit,
                                startIndex: startIndex,
                                fields: EmbyClient.FullSyncItemFields,
                                minDateLastSavedForUser: since);
                            swReq.Stop();

                            long reqDurationMs = swReq.ElapsedMilliseconds;
                            libServerRequestMs += reqDurationMs;
                            Interlocked.Add(ref totalServerRequestMs, reqDurationMs);

                            if (!itemsResult.IsSuccess)
                            {
                                // Back off batch size immediately
                                batchController.RecordRequest(reqDurationMs, 0, currentLimit, wasSuccess: false);

                                // Transient retry with backoff to protect server and recover from brief blips
                                int retryCount = 0;
                                const int maxRetries = 2;
                                while (!itemsResult.IsSuccess && retryCount < maxRetries && !ct.IsCancellationRequested)
                                {
                                    retryCount++;
                                    NocturneLogger.Warn("SyncEngine", $"[{server.Name} • {view.Name}] Batch request failed at startIndex {startIndex}. Retrying ({retryCount}/{maxRetries}) in {retryCount}s with reduced batch size {batchController.CurrentBatchSize}...");
                                    await Task.Delay(1000 * retryCount, ct);

                                    currentLimit = batchController.CurrentBatchSize;
                                    swReq.Restart();
                                    itemsResult = await client.GetItemsAsync(
                                        parentId: view.Id,
                                        limit: currentLimit,
                                        startIndex: startIndex,
                                        fields: EmbyClient.FullSyncItemFields,
                                        minDateLastSavedForUser: since);
                                    swReq.Stop();
                                    Interlocked.Add(ref totalServerRequestMs, swReq.ElapsedMilliseconds);
                                }

                                if (!itemsResult.IsSuccess)
                                {
                                    NocturneLogger.Error("SyncEngine", $"Network error syncing {server.Name} • {view.Name} at item {startIndex} after {maxRetries} retries. Checkpoint preserved at last committed batch.");
                                    failedServers[server.Id] = 1;
                                    break;
                                }
                            }

                            // Size the next request from a fit of this server's own request times (G1, G2).
                            int nextBatchSize = batchController.RecordRequest(reqDurationMs, itemsResult.Items.Count, currentLimit, wasSuccess: true);
                            NocturneLogger.Info("SyncEngine",
                                $"[{server.Name}] batch {itemsResult.Items.Count}/{currentLimit} in {reqDurationMs} ms; fit {batchController.FixedMs:F0} ms + per item baseline {batchController.BaselineMsPerItem:F2} / current {batchController.CurrentMsPerItem:F2} ms{(batchController.IsWarmingUp ? " (warm-up)" : "")}; next {nextBatchSize}");

                            if (itemsResult.Items.Count == 0)
                            {
                                // Nothing (more) changed: an incremental pass still records that it ran.
                                if (since != null)
                                    await channel.Writer.WriteAsync(new SyncBatch { Server = server, View = view, IsIncremental = true, IsCompleted = true, LibraryTotal = serverTotal }, ct);
                                hasMore = false;
                                break;
                            }

                            foreach (var item in itemsResult.Items)
                            {
                                item.ServerId = server.Id;
                                item.ServerName = server.Name;
                            }

                            startIndex += itemsResult.Items.Count;
                            Interlocked.Add(ref totalItemsFetched, itemsResult.Items.Count);
                            libItemsFetched += itemsResult.Items.Count;
                            int totalInView = itemsResult.TotalRecordCount > 0 ? itemsResult.TotalRecordCount : serverTotal;

                            int sFetched = serverFetchedItems.AddOrUpdate(server.Id, itemsResult.Items.Count, (_, val) => val + itemsResult.Items.Count);
                            int sTotal = serverTotalItems.TryGetValue(server.Id, out var st) ? st : 0;
                            double sPercent = sTotal > 0 ? Math.Min(100.0, ((double)sFetched / sTotal) * 100.0) : 100.0;

                            bool isLibraryComplete = (startIndex >= totalInView || itemsResult.Items.Count < currentLimit);

                            var batch = new SyncBatch
                            {
                                Server = server,
                                View = view,
                                StartIndex = startIndex - itemsResult.Items.Count,
                                NextStartIndex = startIndex,
                                TotalInView = totalInView,
                                Items = itemsResult.Items.Where(i => i.Type != "Series").ToList(), // written ahead
                                IsCompleted = isLibraryComplete,
                                IsIncremental = since != null,
                                LibraryTotal = serverTotal,
                                FetchDurationMs = reqDurationMs
                            };

                            // Backpressure applied here: if writer is busy, await channel pauses fetcher!
                            await channel.Writer.WriteAsync(batch, ct);

                            double overallPercent = totalExpectedItems > 0
                                ? Math.Min(90.0, 10.0 + (((double)Math.Min(totalItemsFetched, totalItemsWritten) / totalExpectedItems) * 78.0))
                                : 10.0;

                            var msg = $"{view.Name}: {startIndex:N0} / {totalInView:N0} items";
                            ProgressReported?.Invoke(new SyncProgressReport
                            {
                                State = SyncState.Fetching,
                                ServerId = server.Id,
                                ServerName = server.Name,
                                LibraryName = view.Name,
                                CurrentItem = startIndex,
                                TotalItems = totalInView,
                                ItemsFetched = totalItemsFetched,
                                ItemsWritten = totalItemsWritten,
                                Percentage = overallPercent,
                                ServerPercentage = sPercent,
                                BatchSize = currentLimit,
                                LatencyMs = reqDurationMs,
                                TotalServersCount = servers.Count,
                                ActiveServersCount = Math.Max(1, servers.Count - failedServers.Count),
                                StatusMessage = msg
                            });

                            if (isLibraryComplete)
                            {
                                hasMore = false;
                            }
                        }

                        libSw.Stop();
                        NocturneLogger.Info("SyncEngine", $"[sync:timing] Library '{server.Name} • {view.Name}' ({libItemsFetched:N0} items): Server Request: {libServerRequestMs:N0}ms, Detail Fetches: 0ms (N+1 eliminated), Image Downloads: 0ms (lazy on-screen), Total Library Elapsed: {libSw.ElapsedMilliseconds:N0}ms");
                    }

                    // Signal completion for this specific server
                    int sFinalTotal = serverTotalItems.TryGetValue(server.Id, out var sft) ? sft : 0;
                    ProgressReported?.Invoke(new SyncProgressReport
                    {
                        State = SyncState.Fetching,
                        ServerId = server.Id,
                        ServerName = server.Name,
                        LibraryName = "All libraries synced",
                        CurrentItem = sFinalTotal,
                        TotalItems = sFinalTotal,
                        ItemsFetched = totalItemsFetched,
                        ItemsWritten = totalItemsWritten,
                        Percentage = totalExpectedItems > 0
                            ? Math.Min(90.0, 10.0 + (((double)Math.Min(totalItemsFetched, totalItemsWritten) / totalExpectedItems) * 78.0))
                            : 90.0,
                        ServerPercentage = 100.0,
                        BatchSize = 0,
                        LatencyMs = 0,
                        TotalServersCount = servers.Count,
                        ActiveServersCount = Math.Max(1, servers.Count - failedServers.Count),
                        StatusMessage = "All libraries synced"
                    });
                }
                finally
                {
                    serverSemaphore.Release();
                }
            });

            // Wait for all parallel server fetcher tasks to complete
            await Task.WhenAll(serverTasks);

            // Signal producer completion to writer task and wait for writer to drain all batches
            channel.Writer.Complete();
            await writerTask;

            // Untagged episodes sat in held groups of their own during the sync (ConcurrentDedupIndex.HeldGroup):
            // place them now that every tagged copy is in, then drop the emptied held groups. Also picks up rows an
            // interrupted sync left held or ungrouped.
            var toPlace = _db.GetItemsToPlace();
            if (toPlace.Count > 0)
            {
                _dedupIndex.ProcessBatch(toPlace, _db);
                NocturneLogger.Info("SyncEngine", $"Placed {toPlace.Count:N0} held episodes; {_db.DeleteEmptyHeldGroups():N0} held groups released.");
            }

            syncSw.Stop();

            // Benchmark summary log
            var benchmarkSummary = new SyncBenchmarkMetrics
            {
                ServerRequestMs = totalServerRequestMs,
                DetailEnrichmentMs = 0,
                ImageDownloadMs = 0,
                SqliteWriteMs = totalSqliteWriteMs,
                TotalElapsedMs = syncSw.ElapsedMilliseconds,
                ItemsProcessed = totalItemsWritten
            };
            NocturneLogger.Info("SyncEngine", benchmarkSummary.ToString());

            // ── Crash safety & Watermark rule: Check for failed servers ──
            if (!failedServers.IsEmpty)
            {
                _settings.Update(s => s.SyncStatus = "partial");
                var failedNames = string.Join(", ", servers.Where(sv => failedServers.ContainsKey(sv.Id)).Select(sv => sv.Name));
                var partialMsg = $"Sync incomplete: {failedNames} could not be synced. Progress is kept and the next sync retries.";
                NocturneLogger.Warn("SyncEngine", partialMsg);
                SetState(SyncState.Partial, partialMsg);
                ProgressReported?.Invoke(new SyncProgressReport
                {
                    State = SyncState.Partial,
                    StatusMessage = partialMsg,
                    Percentage = 100.0,
                    ItemsFetched = totalItemsFetched,
                    ItemsWritten = totalItemsWritten,
                    TotalServersCount = servers.Count,
                    ActiveServersCount = Math.Max(0, servers.Count - failedServers.Count)
                });
                return;
            }

            // Auto-maintain CombinedMappings across all servers
            try
            {
                var movieLibs = new List<CombinedLibraryRef>();
                var showLibs = new List<CombinedLibraryRef>();

                foreach (var (srv, srvViews) in discoveredServerViews)
                {
                    foreach (var v in srvViews)
                    {
                        var nameLower = (v.Name ?? "").ToLowerInvariant();
                        var typeLower = (v.CollectionType ?? "").ToLowerInvariant();

                        if (typeLower == "movies" || nameLower.Contains("movie") || nameLower.Contains("film") || nameLower.Contains("cinema"))
                        {
                            movieLibs.Add(new CombinedLibraryRef
                            {
                                ServerId = srv.Id,
                                ServerName = srv.Name,
                                LibraryId = v.Id,
                                LibraryName = v.Name
                            });
                        }
                        else if (typeLower == "tvshows" || nameLower.Contains("show") || nameLower.Contains("series") || nameLower.Contains("tv"))
                        {
                            showLibs.Add(new CombinedLibraryRef
                            {
                                ServerId = srv.Id,
                                ServerName = srv.Name,
                                LibraryId = v.Id,
                                LibraryName = v.Name
                            });
                        }
                    }
                }

                _settings.Update(s =>
                {
                    if (!s.CombinedMappings.ContainsKey("movies"))
                    {
                        s.CombinedMappings["movies"] = new CombinedMapping { Name = "Movies", Icon = "Film", Libraries = movieLibs };
                    }
                    else
                    {
                        var existing = s.CombinedMappings["movies"];
                        foreach (var ml in movieLibs)
                        {
                            if (!existing.Libraries.Any(l => l.ServerId == ml.ServerId && l.LibraryId == ml.LibraryId))
                            {
                                existing.Libraries.Add(ml);
                            }
                        }
                    }

                    if (!s.CombinedMappings.ContainsKey("shows"))
                    {
                        s.CombinedMappings["shows"] = new CombinedMapping { Name = "Shows", Icon = "Tv", Libraries = showLibs };
                    }
                    else
                    {
                        var existing = s.CombinedMappings["shows"];
                        foreach (var sl in showLibs)
                        {
                            if (!existing.Libraries.Any(l => l.ServerId == sl.ServerId && l.LibraryId == sl.LibraryId))
                            {
                                existing.Libraries.Add(sl);
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("SyncEngine", $"Error updating combined mappings: {ex.Message}");
            }

            // Sync Trakt Watchlist if Trakt is connected
            if (_trakt.IsConnected)
            {
                try
                {
                    var traktMsg = "Syncing Trakt Watchlist...";
                    SetState(SyncState.Fetching, traktMsg);
                    ProgressReported?.Invoke(new SyncProgressReport
                    {
                        State = SyncState.Fetching,
                        ServerId = "trakt",
                        ServerName = "Trakt.tv",
                        StatusMessage = traktMsg,
                        Percentage = 88.0,
                        ServerPercentage = 100.0,
                        TotalServersCount = servers.Count,
                        ActiveServersCount = servers.Count
                    });

                    var watchlist = await _trakt.GetWatchlistAsync();
                    if (watchlist.Count > 0)
                    {
                        _db.SaveTraktWatchlist(watchlist);
                        NocturneLogger.Info("SyncEngine", $"Synced {watchlist.Count} Trakt watchlist items to SQLite.");
                    }
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("SyncEngine", $"Trakt watchlist sync error: {ex.Message}");
                }
            }

            // Finalize multi-server deduplication index (built concurrently during sync)
            _settings.Update(s => s.DedupStatus = "in-progress");
            _dedupIndex.FlushAll(_db);
            _settings.Update(s => s.DedupStatus = "complete");

            int totalIndexed = _db.GetCachedItemsCount();
            var multiVersionGroups = _db.CountMultiVersionGroups();
            var doneMsg = $"Sync complete ({totalIndexed:N0} items from {servers.Count} server(s), {multiVersionGroups:N0} deduplicated titles).";

            _settings.Update(s =>
            {
                s.SyncStatus = "complete";
                s.LastFullSync = DateTime.UtcNow.ToString("o");
            });

            SetState(SyncState.Done, doneMsg);
            ProgressReported?.Invoke(new SyncProgressReport
            {
                State = SyncState.Done,
                StatusMessage = doneMsg,
                Percentage = 100.0,
                ServerPercentage = 100.0,
                ItemsFetched = totalItemsFetched,
                ItemsWritten = totalItemsWritten,
                TotalServersCount = servers.Count,
                ActiveServersCount = servers.Count
            });
            NocturneLogger.Info("SyncEngine", $"Multi-server sync completed successfully: {totalIndexed} items indexed across {servers.Count} server(s), {multiVersionGroups} multi-version deduplicated groups.");
            SyncCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("SyncEngine", "Sync encountered an error", ex);
            _settings.Update(s => s.SyncStatus = "failed");
            var errMsg = $"Sync failed: {ex.Message}";
            SetState(SyncState.Error, errMsg);
            ProgressReported?.Invoke(new SyncProgressReport
            {
                State = SyncState.Error,
                StatusMessage = errMsg,
                Percentage = 0
            });
        }
    }

    private void SetState(SyncState state, string message)
    {
        CurrentState = state;
        StateChanged?.Invoke(state, message);
    }
}
