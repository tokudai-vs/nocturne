using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
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

    public string PlayButtonText => (Item?.UserData?.PlaybackPositionTicks ?? 0) > 0 && !IsPlayed ? "Resume" : "Play";
    public string PlayedButtonText => IsPlayed ? "Watched" : "Mark Watched";

    public bool HasTrailer => Item?.RemoteTrailers != null && Item.RemoteTrailers.Count > 0;
    public bool HasMultipleVersions => GroupVersions.Count > 1;
    public bool IsSeries => Item?.Type == "Series";
    public bool HasSeasons => Seasons.Count > 0;
    public bool HasEpisodes => Episodes.Count > 0;
    public bool HasCast => People.Count > 0;
    public bool HasSimilar => SimilarItems.Count > 0;

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
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(PlayedButtonText));

            var primaryTag = item.ImageTags?.GetValueOrDefault("Primary");
            PosterUrl = _embyClient.BuildImageUrl(item.Id, "Primary", 500, primaryTag);

            var backdropTag = item.BackdropImageTags?.FirstOrDefault() ?? primaryTag;
            BackdropUrl = _embyClient.BuildImageUrl(item.Id, "Backdrop", 1920, backdropTag);

            // Cast & Crew
            Application.Current.Dispatcher.Invoke(() =>
            {
                People.Clear();
                if (item.People != null)
                {
                    foreach (var p in item.People.Take(18))
                    {
                        if (!string.IsNullOrEmpty(p.PrimaryImageTag))
                        {
                            p.ImageUrl = _embyClient.BuildImageUrl(p.Id ?? string.Empty, "Primary", 200, p.PrimaryImageTag);
                        }
                        People.Add(p);
                    }
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
            });

            OnPropertyChanged(nameof(IsSeries));
            OnPropertyChanged(nameof(HasMultipleVersions));
            OnPropertyChanged(nameof(HasTrailer));
            OnPropertyChanged(nameof(HasCast));

            // If it's a TV series, fetch seasons
            if (item.Type == "Series")
            {
                var seasons = await _embyClient.GetSeasonsAsync(item.Id);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Seasons.Clear();
                    foreach (var s in seasons) Seasons.Add(s);
                    OnPropertyChanged(nameof(Seasons));
                    OnPropertyChanged(nameof(HasSeasons));
                    if (Seasons.Count > 0)
                    {
                        SelectedSeason = Seasons[0];
                    }
                });
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Seasons.Clear();
                    Episodes.Clear();
                    OnPropertyChanged(nameof(Seasons));
                    OnPropertyChanged(nameof(Episodes));
                    OnPropertyChanged(nameof(HasSeasons));
                    OnPropertyChanged(nameof(HasEpisodes));
                });
            }

            // Fetch similar items
            var similar = await _embyClient.GetSimilarAsync(item.Id, 12);
            Application.Current.Dispatcher.Invoke(() =>
            {
                SimilarItems.Clear();
                foreach (var sim in similar) SimilarItems.Add(sim);
                OnPropertyChanged(nameof(SimilarItems));
                OnPropertyChanged(nameof(HasSimilar));
            });

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
            Application.Current.Dispatcher.Invoke(() =>
            {
                Episodes.Clear();
                foreach (var ep in eps)
                {
                    var tag = ep.ImageTags?.GetValueOrDefault("Primary");
                    ep.PrimaryImageUrl = _embyClient.BuildImageUrl(ep.Id, "Primary", 400, tag);
                    ep.SeriesImdbId = Item?.ImdbId;
                    Episodes.Add(ep);
                }
                OnPropertyChanged(nameof(Episodes));
                OnPropertyChanged(nameof(HasEpisodes));
            });
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DetailViewModel", $"Episodes load failed for season {seasonId}", ex);
        }
    }

    public event Action<EmbyItem, long>? PlayRequested;

    public async Task PlayAsync(EmbyItem? target = null)
    {
        var playItem = target ?? Item;
        if (playItem == null) return;

        // If series, resolve to first unwatched episode
        if (playItem.Type == "Series")
        {
            EmbyItem? ep = null;
            if (Episodes.Count > 0)
            {
                ep = Episodes.FirstOrDefault(e => !e.IsPlayed) ?? Episodes[0];
            }
            else
            {
                var seasons = Seasons.Count > 0 ? Seasons.ToList() : await _embyClient.GetSeasonsAsync(playItem.Id);
                var season = SelectedSeason ?? seasons.FirstOrDefault();
                if (season != null)
                {
                    var eps = await _embyClient.GetEpisodesAsync(playItem.Id, season.Id);
                    ep = eps.FirstOrDefault(e => !e.IsPlayed) ?? eps.FirstOrDefault();
                }
            }

            if (ep != null)
            {
                playItem = ep;
            }
            else
            {
                NocturneLogger.Warn("DetailViewModel", $"No playable episodes found for series {playItem.Name}");
                return;
            }
        }

        var startTicks = playItem.UserData?.PlaybackPositionTicks ?? 0;

        if (PlayRequested != null)
        {
            PlayRequested.Invoke(playItem, startTicks);
            return;
        }

        var mediaSourceId = playItem.MediaSources?.FirstOrDefault()?.Id ?? playItem.Id;
        var streamUrl = _embyClient.GetStreamUrl(playItem.Id, mediaSourceId);

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
            if (Item.UserData == null) Item.UserData = new UserItemData();
            Item.UserData.Played = newPlayed;
            if (newPlayed) Item.UserData.PlaybackPositionTicks = 0;
            Item.OnPropertyChanged(nameof(EmbyItem.IsPlayed));
            Item.OnPropertyChanged(nameof(EmbyItem.HasResumeProgress));
            Item.OnPropertyChanged(nameof(EmbyItem.ProgressPercentage));
            Item.OnPropertyChanged(nameof(EmbyItem.UserData));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(PlayedButtonText));
        }
    }

    public async Task ToggleEpisodePlayedAsync(EmbyItem ep)
    {
        if (ep == null) return;
        var newPlayed = !ep.IsPlayed;
        var success = newPlayed
            ? await _embyClient.MarkPlayedAsync(ep.Id)
            : await _embyClient.MarkUnplayedAsync(ep.Id);

        if (success)
        {
            if (ep.UserData == null) ep.UserData = new UserItemData();
            ep.UserData.Played = newPlayed;
            if (newPlayed) ep.UserData.PlaybackPositionTicks = 0;
            ep.OnPropertyChanged(nameof(EmbyItem.IsPlayed));
            ep.OnPropertyChanged(nameof(EmbyItem.HasResumeProgress));
            ep.OnPropertyChanged(nameof(EmbyItem.ProgressPercentage));
            ep.OnPropertyChanged(nameof(EmbyItem.UserData));
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
