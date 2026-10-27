using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class WatchlistViewModel : BaseViewModel
{
    private readonly IWatchlistService _watchlist;
    private readonly DatabaseService _db;
    private readonly ITraktAuthService _traktAuth;
    private readonly SettingsService _settings;
    private readonly EmbyClient _emby;

    private bool _isLoading;
    private string _filter = "All";
    private string _sortBy = "DateAdded";
    private string _sortOrder = "Descending";

    public ObservableCollection<WatchlistCardItem> Items { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string SortBy
    {
        get => _sortBy;
        set
        {
            if (SetProperty(ref _sortBy, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public string SortOrder
    {
        get => _sortOrder;
        set
        {
            if (SetProperty(ref _sortOrder, value))
            {
                _ = RefreshAsync();
            }
        }
    }

    public WatchlistViewModel(
        IWatchlistService watchlist,
        DatabaseService db,
        ITraktAuthService traktAuth,
        SettingsService settings,
        EmbyClient emby)
    {
        _watchlist = watchlist;
        _db = db;
        _traktAuth = traktAuth;
        _settings = settings;
        _emby = emby;
    }

    public async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var rawCards = await _watchlist.GetAsync(CancellationToken.None);

            // Filter
            var filtered = rawCards.AsEnumerable();
            if (string.Equals(_filter, "Movies", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(c => string.Equals(c.ItemType, "Movie", StringComparison.OrdinalIgnoreCase));
            }
            else if (string.Equals(_filter, "Shows", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(_filter, "Series", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(c => string.Equals(c.ItemType, "Series", StringComparison.OrdinalIgnoreCase));
            }

            // Sort
            if (_sortBy == "Title")
            {
                filtered = _sortOrder == "Descending"
                    ? filtered.OrderByDescending(c => c.Title)
                    : filtered.OrderBy(c => c.Title);
            }
            else if (_sortBy == "Year")
            {
                filtered = _sortOrder == "Descending"
                    ? filtered.OrderByDescending(c => c.Year ?? 0)
                    : filtered.OrderBy(c => c.Year ?? 0);
            }
            else // DateAdded
            {
                filtered = _sortOrder == "Ascending"
                    ? filtered.OrderBy(c => c.AddedUtc)
                    : filtered.OrderByDescending(c => c.AddedUtc);
            }

            var cardList = filtered.Select(c =>
            {
                string? posterUrl = null;
                // Order 1: Owning server (§3.3)
                if (!string.IsNullOrEmpty(c.ImageServerId) && !string.IsNullOrEmpty(c.ImageItemId) && !string.IsNullOrEmpty(c.ImageTag))
                {
                    var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == c.ImageServerId);
                    if (srv != null)
                    {
                        posterUrl = _emby.BuildImageUrl(c.ImageItemId, "Primary", 320, c.ImageTag, srv.Url, srv.AccessToken);
                    }
                }

                // Order 2: TMDB poster (§3.3)
                if (string.IsNullOrEmpty(posterUrl) && !string.IsNullOrEmpty(c.TmdbPosterPath))
                {
                    posterUrl = $"https://image.tmdb.org/t/p/w342{c.TmdbPosterPath}";
                }

                return new WatchlistCardItem
                {
                    Card = c,
                    Title = c.Title,
                    YearText = c.Year.HasValue ? c.Year.Value.ToString() : string.Empty,
                    PosterUrl = posterUrl,
                    IsInLibrary = c.IsInLibrary,
                    StatusText = c.IsInLibrary ? string.Empty : "Not in your library" // §9.4; owned cards carry no marker, like a library shelf
                };
            }).ToList();

            Items.Clear();
            foreach (var item in cardList)
            {
                Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("WatchlistViewModel", "Failed to refresh watchlist", ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    // Through both peers (§9.3); the pending remove hides the card at once. A title outside the library used to be
    // deleted locally only, so the next Trakt pull brought it back.
    public async Task RemoveFromWatchlistAsync(WatchlistCard card)
    {
        var main = (Application.Current as App)?.Services.GetService<MainViewModel>();
        if (main != null) await main.RemoveWatchlistCardAsync(card);
        await RefreshAsync();
    }
}

public class WatchlistCardItem
{
    public WatchlistCard Card { get; set; } = new();
    public string Title { get; set; } = string.Empty;
    public string YearText { get; set; } = string.Empty;
    public string? PosterUrl { get; set; }
    public bool IsInLibrary { get; set; }
    public string StatusText { get; set; } = string.Empty;
}
