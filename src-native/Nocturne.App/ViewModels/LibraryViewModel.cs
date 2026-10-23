using System.Collections.ObjectModel;
using System.Windows;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class LibraryViewModel : BaseViewModel
{
    private readonly EmbyClient _embyClient;
    private readonly MpvController _mpv;
    private readonly SettingsService _settings;
    private readonly DatabaseService _db;
    private readonly TraktService _trakt;
    private readonly DedupEngine _dedup;

    private EmbyItem? _selectedView;
    private bool _isLoading;
    private string _sortBy = "SortName";
    private string _sortOrder = "Ascending";

    public ObservableCollection<EmbyItem> Views { get; } = new();
    public ObservableCollection<EmbyItem> Items { get; } = new();

    public event Action<EmbyItem>? NavigateToDetail;

    public LibraryViewModel(
        EmbyClient embyClient,
        MpvController mpv,
        SettingsService settings,
        DatabaseService db,
        TraktService trakt,
        DedupEngine dedup)
    {
        _embyClient = embyClient;
        _mpv = mpv;
        _settings = settings;
        _db = db;
        _trakt = trakt;
        _dedup = dedup;
    }

    public EmbyItem? SelectedView
    {
        get => _selectedView;
        set
        {
            if (SetProperty(ref _selectedView, value) && value != null)
            {
                _ = LoadItemsForViewAsync(value.Id);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public string SortBy
    {
        get => _sortBy;
        set
        {
            if (SetProperty(ref _sortBy, value) && SelectedView != null)
            {
                _ = LoadItemsForViewAsync(SelectedView.Id);
            }
        }
    }

    public string SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetProperty(ref _sortOrder, value) && SelectedView != null)
            {
                _ = LoadItemsForViewAsync(SelectedView.Id);
            }
        }
    }

    private async Task EnsureCombinedMappingsAsync()
    {
        if (_settings.Current.CombinedMappings.Count > 0) return;

        var movieLibs = new List<CombinedLibraryRef>();
        var showLibs = new List<CombinedLibraryRef>();

        foreach (var srv in _settings.Current.Servers)
        {
            var client = (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated)
                ? _embyClient
                : EmbyClient.CreateForServer(srv);

            try
            {
                var views = await client.GetViewsAsync();
                foreach (var v in views)
                {
                    var name = (v.Name ?? string.Empty).ToLowerInvariant();
                    var coll = (v.CollectionType ?? string.Empty).ToLowerInvariant();

                    if (coll == "movies" || name.Contains("movie") || name.Contains("film"))
                    {
                        movieLibs.Add(new CombinedLibraryRef
                        {
                            ServerId = srv.Id,
                            ServerName = srv.Name,
                            LibraryId = v.Id,
                            LibraryName = v.Name
                        });
                    }
                    else if (coll == "tvshows" || name.Contains("show") || name.Contains("tv") || name.Contains("series"))
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
            catch (Exception ex)
            {
                NocturneLogger.Warn("LibraryViewModel", $"EnsureCombinedMappings: error fetching views for {srv.Name}: {ex.Message}");
            }
        }

        if (movieLibs.Count > 0 || showLibs.Count > 0)
        {
            _settings.Update(s =>
            {
                if (movieLibs.Count > 0)
                    s.CombinedMappings["movies"] = new CombinedMapping { Name = "Movies", Icon = "Film", Libraries = movieLibs };
                if (showLibs.Count > 0)
                    s.CombinedMappings["shows"] = new CombinedMapping { Name = "Shows", Icon = "Tv", Libraries = showLibs };
            });
        }
    }

    public async Task LoadViewsAsync()
    {
        if (!_embyClient.IsAuthenticated && _settings.Current.Servers.Count == 0) return;
        IsLoading = true;
        try
        {
            Views.Clear();

            // 1. Support Combined Virtual Libraries
            if (_settings.Current.LibraryMode == "combined")
            {
                await EnsureCombinedMappingsAsync();

                foreach (var kv in _settings.Current.CombinedMappings)
                {
                    Views.Add(new EmbyItem
                    {
                        Id = "combined:" + kv.Key,
                        Name = kv.Value.Name,
                        Type = "VirtualFolder"
                    });
                }

                if (_settings.Current.ShowUnmappedLibraries)
                {
                    var mappedLibIds = new HashSet<string>(_settings.Current.CombinedMappings.Values
                        .SelectMany(m => m.Libraries)
                        .Select(l => $"{l.ServerId}:{l.LibraryId}"));

                    foreach (var srv in _settings.Current.Servers)
                    {
                        var client = (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated)
                            ? _embyClient
                            : EmbyClient.CreateForServer(srv);

                        try
                        {
                            var serverViews = await client.GetViewsAsync();
                            foreach (var v in serverViews)
                            {
                                if (!mappedLibIds.Contains($"{srv.Id}:{v.Id}"))
                                {
                                    Views.Add(new EmbyItem
                                    {
                                        Id = $"unmapped:{srv.Id}:{v.Id}",
                                        Name = $"{v.Name} ({srv.Name})",
                                        Type = "VirtualFolder",
                                        ServerId = srv.Id,
                                        ServerName = srv.Name
                                    });
                                }
                            }
                        }
                        catch { }
                    }
                }
            }
            else
            {
                var views = await _embyClient.GetViewsAsync();
                foreach (var v in views) Views.Add(v);
            }

            // 2. Add Trakt Watchlist virtual library shelf if enabled and connected
            if (_trakt.IsConnected && _settings.Current.TraktShowWatchlistInSidebar)
            {
                Views.Add(new EmbyItem
                {
                    Id = "trakt:watchlist",
                    Name = "Trakt Watchlist",
                    Type = "VirtualFolder"
                });
            }

            if (SelectedView == null && Views.Count > 0)
            {
                SelectedView = Views[0];
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("LibraryViewModel", "Failed to load library views", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadItemsForViewAsync(string viewId)
    {
        IsLoading = true;
        try
        {
            Items.Clear();

            if (viewId == "trakt:watchlist")
            {
                var watchlist = _db.GetTraktWatchlist();
                foreach (var w in watchlist)
                {
                    Items.Add(new EmbyItem
                    {
                        Id = w.Id,
                        Name = w.Title,
                        ProductionYear = w.Year,
                        Type = w.Type
                    });
                }
            }
            else if (viewId.StartsWith("combined:"))
            {
                var groupKey = viewId.Substring("combined:".Length);
                var mapping = _settings.Current.CombinedMappings.GetValueOrDefault(groupKey);
                var libraryIds = mapping?.Libraries?.Select(l => l.LibraryId).ToList() ?? new List<string>();

                string? typeFilter = null;
                if (groupKey.Contains("movie") || mapping?.Name.ToLowerInvariant().Contains("movie") == true)
                {
                    typeFilter = "Movie";
                }
                else if (groupKey.Contains("show") || groupKey.Contains("tv") || mapping?.Name.ToLowerInvariant().Contains("show") == true)
                {
                    typeFilter = "Series";
                }

                // Try instant cache first
                var cached = _db.GetCombinedLibraryItems(libraryIds, type: typeFilter, sortBy: SortBy, sortOrder: SortOrder, limit: 200);
                if (cached.Count > 0)
                {
                    Artwork.Apply(cached, _db, _settings.Current.Servers); // spec §3.3: the group's elected poster
                    foreach (var item in cached) Items.Add(item);
                }

                // Fan out live queries across all constituent servers in parallel
                if (mapping != null && mapping.Libraries.Count > 0)
                {
                    var fetchTasks = mapping.Libraries.Select(async libRef =>
                    {
                        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == libRef.ServerId);
                        if (srv == null) return new List<EmbyItem>();
                        var client = (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated)
                            ? _embyClient
                            : EmbyClient.CreateForServer(srv);

                        try
                        {
                            var res = await client.GetItemsAsync(
                                parentId: libRef.LibraryId,
                                limit: 120,
                                sortBy: SortBy,
                                sortOrder: SortOrder,
                                fields: EmbyClient.LightItemFields);

                            foreach (var it in res.Items)
                            {
                                it.ServerId = srv.Id;
                                it.ServerName = srv.Name;
                            }
                            return res.Items;
                        }
                        catch (Exception ex)
                        {
                            NocturneLogger.Warn("LibraryViewModel", $"Failed to fetch items from {srv.Name} ({libRef.LibraryName}): {ex.Message}");
                            return new List<EmbyItem>();
                        }
                    });

                    var allResults = await Task.WhenAll(fetchTasks);
                    var allFetched = allResults.SelectMany(x => x).ToList();

                    if (allFetched.Count > 0)
                    {
                        var primaries = _dedup.Deduplicate(allFetched).Select(g => g.PrimaryItem).ToList();
                        Artwork.Apply(primaries, _db, _settings.Current.Servers);
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            Items.Clear();
                            foreach (var item in primaries) Items.Add(item);
                        });
                    }
                }
            }
            else if (viewId.StartsWith("unmapped:"))
            {
                // Format: unmapped:{serverId}:{libraryId}
                var parts = viewId.Split(':');
                if (parts.Length == 3)
                {
                    var serverId = parts[1];
                    var libId = parts[2];
                    var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == serverId);
                    if (srv != null)
                    {
                        var client = (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated)
                            ? _embyClient
                            : EmbyClient.CreateForServer(srv);

                        var res = await client.GetItemsAsync(parentId: libId, limit: 120, sortBy: SortBy, sortOrder: SortOrder, fields: EmbyClient.LightItemFields);
                        foreach (var it in res.Items) it.ServerId = srv.Id;
                        Artwork.Apply(res.Items, _db, _settings.Current.Servers);
                        foreach (var it in res.Items)
                        {
                            it.ServerId = srv.Id;
                            it.ServerName = srv.Name;
                            Items.Add(it);
                        }
                    }
                }
            }
            else
            {
                // Single Server View
                var activeServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
                var activeSid = activeServer?.Id ?? string.Empty;
                var activeSname = activeServer?.Name ?? string.Empty;

                try
                {
                    var cached = _db.GetCachedItems(parentId: viewId, limit: 100);
                    if (cached.Count > 0)
                    {
                        foreach (var item in cached) Items.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("LibraryViewModel", $"Cache load fallback: {ex.Message}");
                }

                var res = await _embyClient.GetItemsAsync(parentId: viewId, limit: 100, sortBy: SortBy, sortOrder: SortOrder, fields: EmbyClient.LightItemFields);
                if (res.Items.Count > 0 || Items.Count == 0)
                {
                    Items.Clear();
                    foreach (var item in res.Items) item.ServerId = activeSid;
                    Artwork.Apply(res.Items, _db, _settings.Current.Servers);
                    foreach (var item in res.Items)
                    {
                        item.ServerId = activeSid;
                        item.ServerName = activeSname;
                        Items.Add(item);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("LibraryViewModel", $"Failed to load items for view {viewId}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task PlayItemAsync(EmbyItem item)
    {
        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
               ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
        var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
            ? _embyClient
            : EmbyClient.CreateForServer(srv);

        var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
        var streamUrl = client.GetStreamUrl(item.Id, mediaSourceId, srv?.Url, srv?.AccessToken);
        var startTicks = item.UserData?.PlaybackPositionTicks ?? 0;

        await _mpv.PlayAsync(streamUrl, item.Name, startTicks);
        await client.ReportPlaybackStartAsync(item.Id, mediaSourceId, startTicks);
        _db.RecordWatchEvent(item.Id, srv?.Url ?? client.BaseUrl, item.Name, item.Type, (int)((item.RunTimeTicks ?? 0) / 10_000_000), item.SeriesName, item.Genres);
    }

    public void OpenItemDetail(EmbyItem item)
    {
        NavigateToDetail?.Invoke(item);
    }
}
