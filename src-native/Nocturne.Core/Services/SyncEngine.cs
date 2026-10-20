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
    public string ServerName { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public int CurrentItem { get; set; }
    public int TotalItems { get; set; }
    public int ItemsFetched { get; set; }
    public int ItemsWritten { get; set; }
    public double Percentage { get; set; }
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
    public int TotalInView { get; init; }
    public List<EmbyItem> Items { get; init; } = new();
    public bool IsCompleted { get; init; }
    public long FetchDurationMs { get; init; }
}

public class SyncEngine
{
    private readonly EmbyClient _embyClient;
    private readonly DatabaseService _db;
    private readonly DedupEngine _dedup;
    private readonly TraktService _trakt;
    private readonly SettingsService _settings;

    public event Action<SyncState, string>? StateChanged;
    public event Action<int, int>? ProgressChanged;
    public event Action<SyncProgressReport>? ProgressReported;
    public event Action? SyncCompleted;

    public SyncEngine(EmbyClient embyClient, DatabaseService db, DedupEngine dedup, TraktService trakt, SettingsService settings)
    {
        _embyClient = embyClient;
        _db = db;
        _dedup = dedup;
        _trakt = trakt;
        _settings = settings;
    }

    public SyncState CurrentState { get; private set; } = SyncState.Idle;

    public Task SyncAllAsync(CancellationToken ct = default, bool forceResync = false) => RunFullSyncAsync(ct, forceResync);

    public async Task RunFullSyncAsync(CancellationToken ct = default, bool forceResync = false)
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
            var discoveredServerViews = new List<(ServerConfig Server, List<EmbyItem> Views)>();

            // Phase 0: Discovery
            for (int sIndex = 0; sIndex < servers.Count; sIndex++)
            {
                if (ct.IsCancellationRequested) break;
                var server = servers[sIndex];

                var discMsg = $"Discovering libraries from {server.Name} ({sIndex + 1}/{servers.Count})...";
                SetState(SyncState.Discovering, discMsg);
                ProgressReported?.Invoke(new SyncProgressReport
                {
                    State = SyncState.Discovering,
                    ServerName = server.Name,
                    StatusMessage = discMsg,
                    Percentage = servers.Count > 0 ? ((double)sIndex / servers.Count) * 10.0 : 0
                });

                var client = new EmbyClient();
                client.Configure(server.Url, server.AccessToken, server.UserId);

                List<EmbyItem> views;
                try
                {
                    views = await client.GetViewsAsync();
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("SyncEngine", $"Failed to fetch views from server {server.Name}: {ex.Message}");
                    continue;
                }

                discoveredServerViews.Add((server, views));
            }

            int totalViews = discoveredServerViews.Sum(x => x.Views.Count);
            int currentViewIndex = 0;

            // Pre-calculate total items expected across all libraries to give an accurate percentage
            int totalExpectedItems = 0;
            int totalProcessedItems = 0;

            // Probe counts
            var probedViews = new List<(ServerConfig Server, EmbyItem View, int ServerTotal, bool NeedsFetch, int StartIndex)>();
            foreach (var (server, views) in discoveredServerViews)
            {
                var client = new EmbyClient();
                client.Configure(server.Url, server.AccessToken, server.UserId);

                foreach (var view in views)
                {
                    var checkpoint = _db.GetSyncCheckpoint(server.Id, view.Id);
                    var probe = await client.GetItemsAsync(parentId: view.Id, limit: 1);
                    int serverTotal = probe.IsSuccess ? probe.TotalRecordCount : (checkpoint?.TotalItems ?? 0);
                    totalExpectedItems += serverTotal;

                    bool needsFetch = true;
                    int startIndex = 0;

                    if (!forceResync && checkpoint != null && checkpoint.IsCompleted && probe.IsSuccess && checkpoint.TotalItems == serverTotal && serverTotal > 0)
                    {
                        needsFetch = false;
                        totalProcessedItems += serverTotal;
                    }
                    else if (!forceResync && checkpoint != null && !checkpoint.IsCompleted && checkpoint.StartIndex > 0)
                    {
                        startIndex = checkpoint.StartIndex;
                        totalProcessedItems += startIndex;
                    }

                    probedViews.Add((server, view, serverTotal, needsFetch, startIndex));
                }
            }

            // =========================================================================
            // PHASE 1: PIPELINE — PARALLELIZE NETWORK, SERIALIZE WRITES (Bounded Channel)
            // =========================================================================
            // Channel capacity = 8 batches (8,000 items max in flight). Backpressure bounds memory to <20 MB.
            var channel = Channel.CreateBounded<SyncBatch>(new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = true,
                SingleReader = true
            });

            int totalItemsFetched = 0;
            int totalItemsWritten = 0;
            long totalServerRequestMs = 0;
            long totalSqliteWriteMs = 0;
            var failedServers = new HashSet<string>();
            var syncSw = Stopwatch.StartNew();

            _settings.Update(s => s.SyncStatus = "in-progress");

            // ── Single Consumer / Writer Task (Serialized SQLite Transactions) ──
            string activeServerId = "";
            string activeViewId = "";
            int contiguousWatermark = 0;

            var writerTask = Task.Run(async () =>
            {
                while (await channel.Reader.WaitToReadAsync(ct))
                {
                    while (channel.Reader.TryRead(out var batch))
                    {
                        var swWrite = Stopwatch.StartNew();

                        if (batch.Server.Id != activeServerId || batch.View.Id != activeViewId)
                        {
                            activeServerId = batch.Server.Id;
                            activeViewId = batch.View.Id;
                            contiguousWatermark = batch.StartIndex;
                        }

                        contiguousWatermark += batch.Items.Count;

                        // Atomic crash safety: items + checkpoint committed in the exact same transaction!
                        _db.UpsertItems(
                            batch.Items,
                            batch.Server.Id,
                            batch.View.Id,
                            checkpointStartIndex: contiguousWatermark,
                            checkpointTotalItems: batch.TotalInView,
                            checkpointIsCompleted: batch.IsCompleted);

                        swWrite.Stop();
                        totalSqliteWriteMs += swWrite.ElapsedMilliseconds;
                        totalItemsWritten += batch.Items.Count;

                        double overallPercent = totalExpectedItems > 0
                            ? Math.Min(90.0, 10.0 + (((double)Math.Min(totalItemsFetched, totalItemsWritten) / totalExpectedItems) * 78.0))
                            : 10.0;

                        var msg = $"Saving {batch.Server.Name} • {batch.View.Name} ({contiguousWatermark:N0} / {batch.TotalInView:N0} items)...";
                        SetState(SyncState.Writing, msg);
                        ProgressReported?.Invoke(new SyncProgressReport
                        {
                            State = SyncState.Writing,
                            ServerName = batch.Server.Name,
                            LibraryName = batch.View.Name,
                            CurrentItem = contiguousWatermark,
                            TotalItems = batch.TotalInView,
                            ItemsFetched = totalItemsFetched,
                            ItemsWritten = totalItemsWritten,
                            Percentage = overallPercent,
                            StatusMessage = msg
                        });
                    }
                }
            }, ct);

            // ── Producer / Fetcher (Paginating with FullSyncItemFields) ──
            foreach (var (server, view, serverTotal, needsFetch, initialStartIndex) in probedViews)
            {
                if (ct.IsCancellationRequested) break;
                currentViewIndex++;
                ProgressChanged?.Invoke(currentViewIndex, Math.Max(1, totalViews));

                if (!needsFetch)
                {
                    NocturneLogger.Info("SyncEngine", $"{server.Name} • {view.Name} is already up to date ({serverTotal:N0} items). Skipping network download.");
                    continue;
                }

                var client = new EmbyClient();
                client.Configure(server.Url, server.AccessToken, server.UserId);

                int startIndex = initialStartIndex;
                if (startIndex > 0)
                {
                    NocturneLogger.Info("SyncEngine", $"Resuming sync for {server.Name} • {view.Name} from item {startIndex:N0} / {serverTotal:N0}...");
                }

                const int batchSize = 1000;
                bool hasMore = true;
                bool libraryFetchSuccess = true;

                var libSw = Stopwatch.StartNew();
                long libServerRequestMs = 0;
                int libItemsFetched = 0;

                while (hasMore && !ct.IsCancellationRequested)
                {
                    var swReq = Stopwatch.StartNew();
                    var itemsResult = await client.GetItemsAsync(
                        parentId: view.Id,
                        limit: batchSize,
                        startIndex: startIndex,
                        fields: EmbyClient.FullSyncItemFields);
                    swReq.Stop();

                    libServerRequestMs += swReq.ElapsedMilliseconds;
                    totalServerRequestMs += swReq.ElapsedMilliseconds;

                    if (!itemsResult.IsSuccess)
                    {
                        NocturneLogger.Error("SyncEngine", $"Network error syncing {server.Name} • {view.Name} at item {startIndex}. Checkpoint preserved at last committed batch.");
                        failedServers.Add(server.Id);
                        libraryFetchSuccess = false;
                        break;
                    }

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
                    totalItemsFetched += itemsResult.Items.Count;
                    libItemsFetched += itemsResult.Items.Count;
                    int totalInView = itemsResult.TotalRecordCount > 0 ? itemsResult.TotalRecordCount : serverTotal;

                    bool isLibraryComplete = (startIndex >= totalInView || itemsResult.Items.Count < batchSize);

                    var batch = new SyncBatch
                    {
                        Server = server,
                        View = view,
                        StartIndex = startIndex - itemsResult.Items.Count,
                        TotalInView = totalInView,
                        Items = itemsResult.Items,
                        IsCompleted = isLibraryComplete,
                        FetchDurationMs = swReq.ElapsedMilliseconds
                    };

                    // Backpressure applied here: if writer is busy, await channel pauses fetcher!
                    await channel.Writer.WriteAsync(batch, ct);

                    double overallPercent = totalExpectedItems > 0
                        ? Math.Min(90.0, 10.0 + (((double)Math.Min(totalItemsFetched, totalItemsWritten) / totalExpectedItems) * 78.0))
                        : 10.0 + (((double)currentViewIndex / Math.Max(1, totalViews)) * 78.0);

                    var msg = $"Fetching {server.Name} • {view.Name} ({startIndex:N0} / {totalInView:N0} items)...";
                    SetState(SyncState.Fetching, msg);
                    ProgressReported?.Invoke(new SyncProgressReport
                    {
                        State = SyncState.Fetching,
                        ServerName = server.Name,
                        LibraryName = view.Name,
                        CurrentItem = startIndex,
                        TotalItems = totalInView,
                        ItemsFetched = totalItemsFetched,
                        ItemsWritten = totalItemsWritten,
                        Percentage = overallPercent,
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

            // Signal producer completion to writer task and wait for writer to drain all batches
            channel.Writer.Complete();
            await writerTask;

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
            if (failedServers.Count > 0)
            {
                _settings.Update(s => s.SyncStatus = "partial");
                var partialMsg = $"Sync finished with network errors on {failedServers.Count} server(s). Checkpoints preserved for retry.";
                SetState(SyncState.Partial, partialMsg);
                ProgressReported?.Invoke(new SyncProgressReport
                {
                    State = SyncState.Partial,
                    StatusMessage = partialMsg,
                    Percentage = 100.0,
                    ItemsFetched = totalItemsFetched,
                    ItemsWritten = totalItemsWritten
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
                        StatusMessage = traktMsg,
                        Percentage = 88.0
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

            // Rebuild multi-server deduplication index for Movies and Series
            _settings.Update(s => s.DedupStatus = "in-progress");
            var dedupCandidates = _db.GetItemsForDeduplication();
            var dedupMsg = $"Rebuilding multi-server deduplication index ({dedupCandidates.Count:N0} titles)...";
            SetState(SyncState.Deduping, dedupMsg);
            ProgressReported?.Invoke(new SyncProgressReport
            {
                State = SyncState.Deduping,
                StatusMessage = dedupMsg,
                Percentage = 92.0,
                ItemsFetched = totalItemsFetched,
                ItemsWritten = totalItemsWritten
            });

            var dedupGroups = _dedup.Deduplicate(dedupCandidates);
            _db.SaveDedupGroups(dedupGroups);
            _settings.Update(s => s.DedupStatus = "complete");

            int totalIndexed = _db.GetCachedItemsCount();
            var multiVersionGroups = dedupGroups.Count(g => g.SiblingItems.Count > 0);
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
                ItemsFetched = totalItemsFetched,
                ItemsWritten = totalItemsWritten
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
