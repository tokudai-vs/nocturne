using System.Collections.ObjectModel;
using System.Diagnostics;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class DetailViewModel : BaseViewModel
{
    private readonly EmbyClient _embyClient;
    private readonly MpvController _mpv;
    private readonly DatabaseService _db;
    private readonly TraktService _trakt;
    private readonly IntroDbService _introDb;
    private readonly SettingsService _settings;

    private EmbyItem? _item;
    private string? _posterUrl;
    private string? _backdropUrl;
    private EmbyItem? _selectedSeason;
    private EmbyItem? _selectedVersion;
    private bool _isLoading;
    private bool _isPlayed;
    private bool _isFavorite;
    private bool _isInWatchlist;
    private float? _traktRating;
    private SkipPromptEvent? _activeSkipPrompt;

    public ObservableCollection<EmbyItem> Seasons { get; } = new();
    public ObservableCollection<EmbyItem> Episodes { get; } = new();
    public ObservableCollection<EmbyItem> SimilarItems { get; } = new();
    public ObservableCollection<EmbyItem> GroupVersions { get; } = new();
    public ObservableCollection<BaseItemPerson> People { get; } = new();

    public event Action? NavigateBack;

    public DetailViewModel(
        EmbyClient embyClient,
        MpvController mpv,
        DatabaseService db,
        TraktService trakt,
        IntroDbService introDb,
        SettingsService settings)
    {
        _embyClient = embyClient;
        _mpv = mpv;
        _db = db;
        _trakt = trakt;
        _introDb = introDb;
        _settings = settings;

        _introDb.PromptChanged += prompt => ActiveSkipPrompt = prompt;
        _mpv.PositionChanged += sec => _introDb.OnPositionChanged(sec, _mpv);
        _mpv.NextEpisodeRequested += OnNextEpisodeRequested;
    }

    public EmbyItem? Item
    {
        get => _item;
        set => SetProperty(ref _item, value);
    }

    public string? PosterUrl
    {
        get => _posterUrl;
        set => SetProperty(ref _posterUrl, value);
    }

    public string? BackdropUrl
    {
        get => _backdropUrl;
        set => SetProperty(ref _backdropUrl, value);
    }

    public EmbyItem? SelectedSeason
    {
        get => _selectedSeason;
        set
        {
            if (SetProperty(ref _selectedSeason, value) && value != null && Item != null)
            {
                _ = LoadEpisodesAsync(Item.Id, value.Id);
            }
        }
    }

    public EmbyItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value) && value != null)
            {
                // Swap active item metadata with chosen version
                _ = LoadItemAsync(value.Id);
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsPlayed
    {
        get => _isPlayed;
        set => SetProperty(ref _isPlayed, value);
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set => SetProperty(ref _isFavorite, value);
    }

    public bool IsInWatchlist
    {
        get => _isInWatchlist;
        set => SetProperty(ref _isInWatchlist, value);
    }

    public float? TraktRating
    {
        get => _traktRating;
        set => SetProperty(ref _traktRating, value);
    }

    public SkipPromptEvent? ActiveSkipPrompt
    {
        get => _activeSkipPrompt;
        set => SetProperty(ref _activeSkipPrompt, value);
    }

    public bool HasTrailer => Item?.RemoteTrailers != null && Item.RemoteTrailers.Count > 0;
    public bool HasMultipleVersions => GroupVersions.Count > 1;

    public async Task LoadItemAsync(string itemId)
    {
        IsLoading = true;
        try
        {
            var item = await _embyClient.GetItemAsync(itemId);
            if (item == null) return;

            Item = item;
            IsPlayed = item.UserData?.Played ?? false;
            IsFavorite = item.UserData?.IsFavorite ?? false;

            var primaryTag = item.ImageTags?.GetValueOrDefault("Primary");
            PosterUrl = _embyClient.BuildImageUrl(item.Id, "Primary", 500, primaryTag);

            var backdropTag = item.BackdropImageTags?.FirstOrDefault() ?? primaryTag;
            BackdropUrl = _embyClient.BuildImageUrl(item.Id, "Backdrop", 1920, backdropTag);

            // Cast & Crew
            People.Clear();
            if (item.People != null)
            {
                foreach (var p in item.People.Take(12)) People.Add(p);
            }

            // Check deduplication versions
            GroupVersions.Clear();
            if (!string.IsNullOrEmpty(item.DedupGroupId))
            {
                var siblings = _db.GetGroupVersions(item.DedupGroupId);
                foreach (var sib in siblings) GroupVersions.Add(sib);
                _selectedVersion = GroupVersions.FirstOrDefault(v => v.Id == item.Id) ?? item;
                OnPropertyChanged(nameof(SelectedVersion));
            }
            OnPropertyChanged(nameof(HasMultipleVersions));
            OnPropertyChanged(nameof(HasTrailer));

            // If it's a TV series, fetch seasons
            Seasons.Clear();
            Episodes.Clear();
            if (item.Type == "Series")
            {
                var seasons = await _embyClient.GetSeasonsAsync(item.Id);
                foreach (var s in seasons) Seasons.Add(s);
                if (Seasons.Count > 0)
                {
                    SelectedSeason = Seasons[0];
                }
            }

            // Fetch similar items
            SimilarItems.Clear();
            var similar = await _embyClient.GetSimilarAsync(item.Id, 10);
            foreach (var sim in similar) SimilarItems.Add(sim);

            // Check Trakt Watchlist status
            if (_trakt.IsConnected)
            {
                var watchlist = _db.GetTraktWatchlist();
                IsInWatchlist = watchlist.Any(w =>
                    (!string.IsNullOrEmpty(item.TmdbId) && w.TmdbId == item.TmdbId) ||
                    (!string.IsNullOrEmpty(item.ImdbId) && w.ImdbId == item.ImdbId) ||
                    w.Title.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DetailViewModel", $"LoadItem failed for {itemId}", ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task LoadEpisodesAsync(string seriesId, string seasonId)
    {
        try
        {
            var eps = await _embyClient.GetEpisodesAsync(seriesId, seasonId);
            Episodes.Clear();
            foreach (var ep in eps) Episodes.Add(ep);
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DetailViewModel", $"Episodes load failed for season {seasonId}", ex);
        }
    }

    public async Task PlayAsync(EmbyItem? target = null)
    {
        var playItem = target ?? Item;
        if (playItem == null) return;

        // If series, play first unwatched episode
        if (playItem.Type == "Series" && Episodes.Count > 0)
        {
            playItem = Episodes.FirstOrDefault(e => !e.IsPlayed) ?? Episodes[0];
        }

        var mediaSourceId = playItem.MediaSources?.FirstOrDefault()?.Id ?? playItem.Id;
        var streamUrl = _embyClient.GetStreamUrl(playItem.Id, mediaSourceId);
        var startTicks = playItem.UserData?.PlaybackPositionTicks ?? 0;

        // Pre-load skip segments if it's an episode and has IMDB ID
        if (playItem.Type == "Episode" && !string.IsNullOrEmpty(playItem.ImdbId))
        {
            _ = _introDb.LoadSegmentsForEpisodeAsync(
                playItem.ImdbId,
                playItem.ParentIndexNumber ?? 1,
                playItem.IndexNumber ?? 1);
        }

        await _mpv.PlayAsync(streamUrl, playItem.Name, startTicks);
        await _embyClient.ReportPlaybackStartAsync(playItem.Id, mediaSourceId, startTicks);
        await _trakt.ScrobbleStartAsync(playItem, playItem.ProgressPercentage);
        _db.RecordWatchEvent(playItem.Id, _embyClient.BaseUrl, playItem.Name, playItem.Type, (int)((playItem.RunTimeTicks ?? 0) / 10_000_000), playItem.SeriesName, playItem.Genres);
    }

    public void OnNextEpisodeRequested()
    {
        if (Episodes.Count == 0) return;
        var currentIdx = Episodes.IndexOf(Episodes.FirstOrDefault(e => e.Id == Item?.Id) ?? Episodes[0]);
        if (currentIdx >= 0 && currentIdx + 1 < Episodes.Count)
        {
            var nextEp = Episodes[currentIdx + 1];
            _ = PlayAsync(nextEp);
        }
    }

    public async Task ExecuteSkipPromptAsync()
    {
        if (ActiveSkipPrompt == null) return;
        if (ActiveSkipPrompt.IsNextEpisode)
        {
            OnNextEpisodeRequested();
        }
        else
        {
            await _mpv.SeekTo(ActiveSkipPrompt.TargetSeekSec);
        }
        ActiveSkipPrompt = null;
    }

    public async Task TogglePlayedAsync()
    {
        if (Item == null) return;
        var newPlayed = !IsPlayed;
        var success = newPlayed
            ? await _embyClient.MarkPlayedAsync(Item.Id)
            : await _embyClient.MarkUnplayedAsync(Item.Id);

        if (success)
        {
            IsPlayed = newPlayed;
            if (Item.UserData != null) Item.UserData.Played = newPlayed;
        }
    }

    public async Task ToggleFavoriteAsync()
    {
        if (Item == null) return;
        var newFav = !IsFavorite;
        var success = await _embyClient.ToggleFavoriteAsync(Item.Id, newFav);
        if (success)
        {
            IsFavorite = newFav;
            if (Item.UserData != null) Item.UserData.IsFavorite = newFav;
        }
    }

    public async Task ToggleWatchlistAsync()
    {
        if (Item == null || !_trakt.IsConnected) return;
        var newWatchlist = !IsInWatchlist;
        var success = await _trakt.ToggleWatchlistAsync(Item, newWatchlist);
        if (success)
        {
            IsInWatchlist = newWatchlist;
        }
    }

    public void PlayTrailer()
    {
        var trailer = Item?.RemoteTrailers?.FirstOrDefault();
        if (trailer != null && !string.IsNullOrWhiteSpace(trailer.Url))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = trailer.Url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DetailViewModel", $"Failed to launch trailer: {ex.Message}");
            }
        }
    }

    public void GoBack()
    {
        NavigateBack?.Invoke();
    }
}
