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

    private readonly Func<ServerConfig, EmbyClient> _clientFor;

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
            await RunFullSyncCoreAsync(ct, forceResync);
            if (!ct.IsCancellationRequested) await Task.Run(() => FeatureFlags.MeasureCascadeGate(_db), CancellationToken.None);
        }
        finally
        {
            _dedup.WriterGate.Release();
        }
    }

    // A library's Series rows, paged and written before the library itself (not checkpointed: a resumed sync
    // fetches them again). False on a request that fails after retries.
    private static async Task<bool> SendSeriesFirstAsync(EmbyClient client, ServerConfig server, EmbyItem view, ChannelWriter<SyncBatch> writer, CancellationToken ct)
    {
        const int pageSize = 2000;
        for (int start = 0; !ct.IsCancellationRequested; start += pageSize)
        {
            var page = await client.GetItemsAsync(parentId: view.Id, includeItemTypes: "Series", limit: pageSize, startIndex: start, fields: EmbyClient.FullSyncItemFields);
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

                var list = new List<(ServerConfig Server, EmbyItem View, int ServerTotal, bool NeedsFetch, int StartIndex)>();

                foreach (var view in views)
                {
                    if (ct.IsCancellationRequested) break;
                    var checkpoint = _db.GetSyncCheckpoint(server.Id, view.Id);
                    var probe = await client.GetItemsAsync(parentId: view.Id, limit: 1);
                    int serverTotal = probe.IsSuccess ? probe.TotalRecordCount : (checkpoint?.TotalItems ?? 0);

                    bool needsFetch = true;
                    int startIndex = 0;

                    if (!forceResync && checkpoint != null && checkpoint.IsCompleted && probe.IsSuccess && checkpoint.TotalItems == serverTotal && serverTotal > 0)
                    {
                        needsFetch = false;
                    }
                    else if (!forceResync && checkpoint != null && !checkpoint.IsCompleted && checkpoint.StartIndex > 0)
                    {
                        startIndex = checkpoint.StartIndex;
                    }

                    list.Add((server, view, serverTotal, needsFetch, startIndex));
                }

                return list;
            }));

            var probedViews = probeResults.SelectMany(x => x).ToList();
            int totalViews = probedViews.Count;
            int totalExpectedItems = probedViews.Sum(x => x.ServerTotal);
            int totalProcessedItems = probedViews.Where(x => !x.NeedsFetch).Sum(x => x.ServerTotal) +
                                      probedViews.Where(x => x.NeedsFetch).Sum(x => x.StartIndex);

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
                int sInitial = group.Sum(p => !p.NeedsFetch ? p.ServerTotal : p.StartIndex);
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
                        _dedupIndex.ProcessBatch(batch.Items, _db, deferUntagged: true);

                        if (batch.IsSeriesPrefetch)
                        {
                            _db.UpsertItems(batch.Items, batch.Server.Id, batch.View.Id);
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

                    foreach (var (_, view, serverTotal, needsFetch, initialStartIndex) in group.Views)
                    {
                        if (ct.IsCancellationRequested) break;

                        int doneViews = Interlocked.Increment(ref completedViews);
                        ProgressChanged?.Invoke(doneViews, Math.Max(1, totalViews));

                        if (!needsFetch)
                        {
                            NocturneLogger.Info("SyncEngine", $"{server.Name} • {view.Name} is already up to date ({serverTotal:N0} items). Skipping network download.");
                            continue;
                        }

                        int startIndex = initialStartIndex;
                        if (startIndex > 0)
                        {
                            NocturneLogger.Info("SyncEngine", $"Resuming sync for {server.Name} • {view.Name} from item {startIndex:N0} / {serverTotal:N0} (batch size {batchController.CurrentBatchSize})...");
                        }

                        // Series rows first: an episode deduplicated before its Series row is cached has no series
                        // provider ids (no ep:tvdb/tmdb/imdb keys) and groups differently from a rebuild of the same
                        // items. In SortName order half the live library's episodes came before their series (#20).
                        if (!await SendSeriesFirstAsync(client, server, view, channel.Writer, ct))
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
                                fields: EmbyClient.FullSyncItemFields);
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
                                        fields: EmbyClient.FullSyncItemFields);
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

            // Untagged episodes were written without a group (ConcurrentDedupIndex.IsUntaggedEpisode); place them
            // now that every tagged copy is in. Also picks up any an interrupted sync left ungrouped.
            var ungrouped = _db.GetItemsWithNullGroupId();
            if (ungrouped.Count > 0) _dedupIndex.ProcessBatch(ungrouped, _db);

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
