using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public enum SyncState
{
    Idle,
    Discovering,
    Fetching,
    Deduping,
    Done,
    Error
}

public class SyncEngine
{
    private readonly EmbyClient _embyClient;
    private readonly DatabaseService _db;
    private readonly DedupEngine _dedup;
    private readonly TraktService _trakt;

    public event Action<SyncState, string>? StateChanged;
    public event Action<int, int>? ProgressChanged;

    public SyncEngine(EmbyClient embyClient, DatabaseService db, DedupEngine dedup, TraktService trakt)
    {
        _embyClient = embyClient;
        _db = db;
        _dedup = dedup;
        _trakt = trakt;
    }

    public SyncState CurrentState { get; private set; } = SyncState.Idle;

    public Task SyncAllAsync(CancellationToken ct = default) => RunFullSyncAsync(ct);

    public async Task RunFullSyncAsync(CancellationToken ct = default)
    {
        if (!_embyClient.IsAuthenticated) return;

        try
        {
            SetState(SyncState.Discovering, "Discovering libraries from Emby...");
            var views = await _embyClient.GetViewsAsync();
            var allItems = new List<EmbyItem>();

            SetState(SyncState.Fetching, "Syncing media collections...");
            for (int i = 0; i < views.Count; i++)
            {
                if (ct.IsCancellationRequested) break;
                var view = views[i];
                ProgressChanged?.Invoke(i + 1, views.Count);

                int startIndex = 0;
                const int batchSize = 100;
                bool hasMore = true;

                while (hasMore && !ct.IsCancellationRequested)
                {
                    var itemsResult = await _embyClient.GetItemsAsync(
                        parentId: view.Id,
                        limit: batchSize,
                        startIndex: startIndex);

                    if (itemsResult.Items.Count == 0)
                    {
                        hasMore = false;
                        break;
                    }

                    foreach (var item in itemsResult.Items)
                    {
                        item.ServerId = _embyClient.BaseUrl;
                        allItems.Add(item);
                        _db.UpsertItem(item, _embyClient.BaseUrl);
                    }

                    startIndex += itemsResult.Items.Count;
                    if (startIndex >= itemsResult.TotalRecordCount || itemsResult.Items.Count < batchSize)
                    {
                        hasMore = false;
                    }
                }
            }

            // Sync Trakt Watchlist if Trakt is connected
            if (_trakt.IsConnected)
            {
                try
                {
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

            SetState(SyncState.Deduping, "Rebuilding deduplication index...");
            var dedupGroups = _dedup.Deduplicate(allItems);
            _db.SaveDedupGroups(dedupGroups);

            SetState(SyncState.Done, $"Sync complete ({allItems.Count} items indexed, {dedupGroups.Count} deduplicated titles).");
            NocturneLogger.Info("SyncEngine", $"Sync successfully completed: {allItems.Count} items, {dedupGroups.Count} groups.");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("SyncEngine", "Sync encountered an error", ex);
            SetState(SyncState.Error, $"Sync failed: {ex.Message}");
        }
    }

    private void SetState(SyncState state, string message)
    {
        CurrentState = state;
        StateChanged?.Invoke(state, message);
    }
}
