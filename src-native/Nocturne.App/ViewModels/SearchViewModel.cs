using System.Collections.ObjectModel;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class SearchViewModel : BaseViewModel
{
    private readonly EmbyClient _embyClient;
    private readonly DatabaseService _db;
    private readonly SettingsService _settings;
    private readonly DedupEngine _dedup;
    private string _query = string.Empty;
    private bool _isSearching;

    public ObservableCollection<EmbyItem> Results { get; } = new();

    public event Action<EmbyItem>? NavigateToDetail;

    public SearchViewModel(
        EmbyClient embyClient,
        DatabaseService db,
        SettingsService settings,
        DedupEngine dedup)
    {
        _embyClient = embyClient;
        _db = db;
        _settings = settings;
        _dedup = dedup;
    }

    public string Query
    {
        get => _query;
        set
        {
            if (SetProperty(ref _query, value))
            {
                _ = SearchAsync(value);
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        set => SetProperty(ref _isSearching, value);
    }

    public async Task SearchAsync(string term)
    {
        if (string.IsNullOrWhiteSpace(term))
        {
            Results.Clear();
            return;
        }

        IsSearching = true;
        try
        {
            // 1. Instant local search from SQLite cache
            var localMatches = _db.GetCachedItems(limit: 50)
                .Where(i => i.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                            (i.SeriesName != null && i.SeriesName.Contains(term, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            Results.Clear();
            foreach (var item in localMatches) Results.Add(item);

            // 2. Query Emby servers for full catalog results
            var isCombined = _settings.Current.LibraryMode == "combined" && _settings.Current.Servers.Count > 1;

            if (isCombined)
            {
                var tasks = _settings.Current.Servers.Select(async srv =>
                {
                    var client = (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated)
                        ? _embyClient
                        : EmbyClient.CreateForServer(srv);
                    try
                    {
                        var items = await client.SearchAsync(term, 25);
                        foreach (var it in items)
                        {
                            it.ServerId = srv.Id;
                            it.ServerName = srv.Name;
                            it.PrimaryImageUrl = client.BuildImageUrl(it.Id, "Primary", 320, it.ImageTags?.GetValueOrDefault("Primary"), srv.Url, srv.AccessToken);
                        }
                        return items;
                    }
                    catch { return new List<EmbyItem>(); }
                });

                var serverResults = await Task.WhenAll(tasks);
                var allLive = serverResults.SelectMany(x => x).ToList();

                if (allLive.Count > 0)
                {
                    var deduped = _dedup.Deduplicate(allLive);
                    Results.Clear();
                    foreach (var g in deduped) Results.Add(g.PrimaryItem);
                }
            }
            else if (_embyClient.IsAuthenticated)
            {
                var activeServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
                var serverItems = await _embyClient.SearchAsync(term, 30);
                foreach (var sItem in serverItems)
                {
                    sItem.ServerId = activeServer?.Id;
                    sItem.ServerName = activeServer?.Name;
                    sItem.PrimaryImageUrl = _embyClient.BuildImageUrl(sItem.Id, "Primary", 320, sItem.ImageTags?.GetValueOrDefault("Primary"), activeServer?.Url, activeServer?.AccessToken);
                    if (!Results.Any(r => r.Id == sItem.Id))
                    {
                        Results.Add(sItem);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("SearchViewModel", $"Search failed for '{term}'", ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    public void OpenItemDetail(EmbyItem item)
    {
        NavigateToDetail?.Invoke(item);
    }
}
