using System.Collections.ObjectModel;
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
        TraktService trakt)
    {
        _embyClient = embyClient;
        _mpv = mpv;
        _settings = settings;
        _db = db;
        _trakt = trakt;
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

    public async Task LoadViewsAsync()
    {
        if (!_embyClient.IsAuthenticated) return;
        IsLoading = true;
        try
        {
            Views.Clear();

            // 1. Support Combined Virtual Libraries
            if (_settings.Current.LibraryMode == "combined" && _settings.Current.CombinedMappings.Count > 0)
            {
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
                    var serverViews = await _embyClient.GetViewsAsync();
                    foreach (var v in serverViews)
                    {
                        if (!Views.Any(x => x.Name.Equals(v.Name, StringComparison.OrdinalIgnoreCase)))
                        {
                            Views.Add(v);
                        }
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
                var cached = _db.GetCachedItems(limit: 200);
                foreach (var item in cached) Items.Add(item);

                if (Items.Count == 0)
                {
                    var res = await _embyClient.GetItemsAsync(limit: 100, sortBy: SortBy, sortOrder: SortOrder);
                    foreach (var item in res.Items) Items.Add(item);
                }
            }
            else
            {
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
                    foreach (var item in res.Items) Items.Add(item);
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
        var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
        var streamUrl = _embyClient.GetStreamUrl(item.Id, mediaSourceId);
        var startTicks = item.UserData?.PlaybackPositionTicks ?? 0;

        await _mpv.PlayAsync(streamUrl, item.Name, startTicks);
        await _embyClient.ReportPlaybackStartAsync(item.Id, mediaSourceId, startTicks);
        await _trakt.ScrobbleStartAsync(item, item.ProgressPercentage);
        _db.RecordWatchEvent(item.Id, _embyClient.BaseUrl, item.Name, item.Type, (int)((item.RunTimeTicks ?? 0) / 10_000_000), item.SeriesName, item.Genres);
    }

    public void OpenItemDetail(EmbyItem item)
    {
        NavigateToDetail?.Invoke(item);
    }
}
