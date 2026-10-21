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
    private readonly WatchPartyService _watchParty;
    private readonly TrailerService _trailerService;

    private ServerConfig? _currentServer;
    private EmbyClient _currentClient;

    private EmbyItem? _item;
    private string? _posterUrl;
    private string? _backdropUrl;
    private string? _backgroundTrailerUrl;
    private EmbyItem? _selectedSeason;
    private EmbyItem? _selectedVersion;
    private bool _isLoading;
    private bool _isPlayed;
    private bool _isFavorite;
    private bool _isInWatchlist;
    private float? _traktRating;
    private SkipPromptEvent? _activeSkipPrompt;

    private bool _isWatchPartyModalOpen;
    private bool _isWatchPartyDownloading;
    private string _watchPartyStatusText = "Preparing Watch Party...";
    private int _watchPartyProgress;
    private string _watchPartyGuestsText = "0 guests connected";
    private string _watchPartyTranscodedText = "Buffering stream...";
    private string _selectedQuality = "1080p Full HD";
    private bool _startFromResume = true;
    private string _copyButtonText = "Copy Link";

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
        SettingsService settings,
        WatchPartyService watchParty,
        TrailerService trailerService)
    {
        _embyClient = embyClient;
        _mpv = mpv;
        _db = db;
        _trakt = trakt;
        _introDb = introDb;
        _settings = settings;
        _watchParty = watchParty;
        _trailerService = trailerService;
        _currentClient = embyClient;

        _introDb.PromptChanged += prompt => ActiveSkipPrompt = prompt;
        _mpv.PositionChanged += sec => _introDb.OnPositionChanged(sec, _mpv);

        _watchParty.StateChanged += (s, state) =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                OnPropertyChanged(nameof(IsWatchPartyActive));
                OnPropertyChanged(nameof(WatchPartyTunnelUrl));
                OnPropertyChanged(nameof(WatchPartyStateText));
            });
        };

        _watchParty.GuestCountChanged += (s, count) =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                WatchPartyGuestsText = count == 1 ? "1 guest connected" : $"{count} guests connected";
            });
        };

        _watchParty.TranscodeProgressChanged += (s, sec) =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                WatchPartyTranscodedText = $"Transcoded: {TimeSpan.FromSeconds(sec):mm\\:ss}";
            });
        };

        _watchParty.TunnelUrlResolved += (s, url) =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                OnPropertyChanged(nameof(WatchPartyTunnelUrl));
            });
        };
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

    public string? BackgroundTrailerUrl
    {
        get => _backgroundTrailerUrl;
        set
        {
            if (SetProperty(ref _backgroundTrailerUrl, value))
            {
                OnPropertyChanged(nameof(HasBackgroundTrailer));
                OnPropertyChanged(nameof(HasTrailer));
            }
        }
    }

    public bool HasBackgroundTrailer => !string.IsNullOrEmpty(BackgroundTrailerUrl);

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

    private EmbyClient ResolveClient(string? serverId)
    {
        _currentServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == serverId)
                      ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);

        if (_currentServer == null || (_currentServer.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
        {
            _currentClient = _embyClient;
        }
        else
        {
            _currentClient = EmbyClient.CreateForServer(_currentServer);
        }
        return _currentClient;
    }

    public EmbyItem? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value) && value != null)
            {
                if (value.Id != Item?.Id)
                {
                    _ = LoadItemAsync(value.Id, value.ServerId, value);
                }
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

    public bool HasTrailer => (Item?.RemoteTrailers != null && Item.RemoteTrailers.Count > 0) || !string.IsNullOrEmpty(BackgroundTrailerUrl);
    public bool HasMultipleVersions => GroupVersions.Count > 1;
    public bool IsSeries => Item?.Type == "Series";
    public bool HasSeasons => Seasons.Count > 0;
    public bool HasEpisodes => Episodes.Count > 0;
    public bool HasCast => People.Count > 0;
    public bool HasSimilar => SimilarItems.Count > 0;

    public async Task LoadItemAsync(string itemId, string? serverId = null, EmbyItem? initialItem = null)
    {
        BackgroundTrailerUrl = null;
        var client = ResolveClient(serverId);

        // 1. Instant Cache Layer (0ms!): in-memory initial item or SQLite cache
        var item = initialItem ?? _db.GetCachedItem(itemId, serverId) ?? _db.GetCachedItem(itemId);

        if (item != null)
        {
            item.ServerId = _currentServer?.Id ?? serverId ?? item.ServerId;
            item.ServerName = _currentServer?.Name ?? item.ServerName;
            ApplyItemToUi(item, client);
            IsLoading = false;
        }
        else
        {
            IsLoading = true;
        }

        // 2. Background Refresh: Fetch fresh details from server without blocking the UI
        _ = Task.Run(async () =>
        {
            try
            {
                var fresh = await client.GetItemAsync(itemId);
                if (fresh != null)
                {
                    fresh.ServerId = _currentServer?.Id ?? serverId ?? fresh.ServerId;
                    fresh.ServerName = _currentServer?.Name ?? fresh.ServerName;
                    _db.UpsertItem(fresh, fresh.ServerId ?? "server");

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (Item == null || Item.Id == fresh.Id)
                        {
                            ApplyItemToUi(fresh, client);
                        }
                    });
                }

                // If Series: refresh seasons from server in background
                if (item?.Type == "Series" || fresh?.Type == "Series")
                {
                    var freshSeasons = await client.GetSeasonsAsync(itemId);
                    if (freshSeasons != null && freshSeasons.Count > 0)
                    {
                        _db.UpsertItems(freshSeasons, _currentServer?.Id ?? serverId ?? "server", itemId);
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            if (Item?.Id == itemId)
                            {
                                Seasons.Clear();
                                foreach (var s in freshSeasons)
                                {
                                    s.ServerId = _currentServer?.Id;
                                    s.ServerName = _currentServer?.Name;
                                    Seasons.Add(s);
                                }
                                OnPropertyChanged(nameof(Seasons));
                                OnPropertyChanged(nameof(HasSeasons));
                                if (SelectedSeason == null || !Seasons.Any(s => s.Id == SelectedSeason.Id))
                                {
                                    SelectedSeason = Seasons.FirstOrDefault();
                                }
                            }
                        });
                    }
                }

                // Similar items in background
                var similar = await client.GetSimilarAsync(itemId, 12);
                if (similar != null && similar.Count > 0)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (Item?.Id == itemId)
                        {
                            SimilarItems.Clear();
                            foreach (var sim in similar)
                            {
                                sim.ServerId = _currentServer?.Id;
                                sim.ServerName = _currentServer?.Name;
                                sim.PrimaryImageUrl = client.BuildImageUrl(sim.Id, "Primary", 320, sim.ImageTags?.GetValueOrDefault("Primary"), _currentServer?.Url, _currentServer?.AccessToken);
                                SimilarItems.Add(sim);
                            }
                            OnPropertyChanged(nameof(SimilarItems));
                            OnPropertyChanged(nameof(HasSimilar));
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DetailViewModel", $"Background refresh failed for {itemId}: {ex.Message}");
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => IsLoading = false);
            }
        });
    }

    private void ApplyItemToUi(EmbyItem item, EmbyClient client)
    {
        Item = item;
        IsPlayed = item.UserData?.Played ?? false;
        IsFavorite = item.UserData?.IsFavorite ?? false;
        OnPropertyChanged(nameof(PlayButtonText));
        OnPropertyChanged(nameof(PlayedButtonText));

        // If this is an episode, load parent series from cache or memory to inherit backdrop & cast
        EmbyItem? parentSeries = null;
        if (string.Equals(item.Type, "Episode", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(item.SeriesId))
        {
            parentSeries = _db.GetCachedItem(item.SeriesId);
        }

        var primaryTag = item.ImageTags?.GetValueOrDefault("Primary");
        PosterUrl = client.BuildImageUrl(item.Id, "Primary", 500, primaryTag, _currentServer?.Url, _currentServer?.AccessToken);

        var backdropTag = item.BackdropImageTags?.FirstOrDefault();
        string? backdropId = item.Id;

        if (string.IsNullOrEmpty(backdropTag) && parentSeries != null)
        {
            backdropTag = parentSeries.BackdropImageTags?.FirstOrDefault() ?? parentSeries.ImageTags?.GetValueOrDefault("Primary");
            backdropId = parentSeries.Id;
        }

        if (string.IsNullOrEmpty(backdropTag))
        {
            backdropTag = primaryTag;
        }

        BackdropUrl = !string.IsNullOrEmpty(backdropTag)
            ? client.BuildImageUrl(backdropId, "Backdrop", 1920, backdropTag, _currentServer?.Url, _currentServer?.AccessToken)
            : null;

        // Cast & Crew
        var peopleSource = (item.People != null && item.People.Count > 0)
            ? item.People
            : parentSeries?.People;

        People.Clear();
        if (peopleSource != null)
        {
            foreach (var p in peopleSource.Take(24))
            {
                if (!string.IsNullOrEmpty(p.PrimaryImageTag))
                {
                    p.ImageUrl = client.BuildImageUrl(p.Id ?? string.Empty, "Primary", 200, p.PrimaryImageTag, _currentServer?.Url, _currentServer?.AccessToken);
                }
                People.Add(p);
            }
        }
        OnPropertyChanged(nameof(HasCast));

        // Deduplication siblings & Clean Edition Mapping (Zero Server Leakage)
        GroupVersions.Clear();
        var siblings = _db.GetSiblingVersions(item);
        if (siblings.Count == 0) siblings.Add(item);

        var priorityList = _settings.Current.PreferredServerPriority ?? new List<string>();

        // Order siblings by Playback Decision Matrix: Quality > Bitrate > Server Priority
        var sortedSiblings = siblings
            .OrderByDescending(s => GetQualityRank(s))
            .ThenByDescending(s => s.MeasuredBitrate ?? 0)
            .ThenBy(s =>
            {
                var idx = priorityList.IndexOf(s.ServerId ?? string.Empty);
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ToList();

        // Deduplicate distinct technical editions (e.g. 4K IMAX, 1080p BluRay)
        var distinctEditions = new Dictionary<string, EmbyItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var sib in sortedSiblings)
        {
            var key = sib.TechnicalBadge;
            if (string.IsNullOrWhiteSpace(key)) key = sib.ResolutionBadge ?? "HD";

            if (!distinctEditions.TryGetValue(key, out var rep))
            {
                distinctEditions[key] = sib;
                GroupVersions.Add(sib);
            }
            else
            {
                // Sibling copy with identical quality on another server - add as fallback
                rep.FallbackCopies.Add(sib);
            }
        }

        var matchInGroup = GroupVersions.FirstOrDefault(v => v.Id == item.Id)
                        ?? GroupVersions.FirstOrDefault(v => v.TechnicalBadge == item.TechnicalBadge)
                        ?? GroupVersions.FirstOrDefault();
        _selectedVersion = matchInGroup;
        OnPropertyChanged(nameof(SelectedVersion));

        OnPropertyChanged(nameof(IsSeries));
        OnPropertyChanged(nameof(HasMultipleVersions));
        OnPropertyChanged(nameof(HasTrailer));

        // TV Series Seasons
        if (item.Type == "Series")
        {
            var cachedSeasons = _db.GetCachedChildren(item.Id, "Season");
            if (cachedSeasons.Count > 0)
            {
                Seasons.Clear();
                foreach (var s in cachedSeasons)
                {
                    s.ServerId = _currentServer?.Id;
                    s.ServerName = _currentServer?.Name;
                    Seasons.Add(s);
                }
                OnPropertyChanged(nameof(Seasons));
                OnPropertyChanged(nameof(HasSeasons));
                if (SelectedSeason == null || !Seasons.Any(s => s.Id == SelectedSeason.Id))
                {
                    SelectedSeason = Seasons.FirstOrDefault();
                }
            }
        }
        else
        {
            Seasons.Clear();
            Episodes.Clear();
            OnPropertyChanged(nameof(Seasons));
            OnPropertyChanged(nameof(Episodes));
            OnPropertyChanged(nameof(HasSeasons));
            OnPropertyChanged(nameof(HasEpisodes));
        }

        // Trakt watchlist
        if (_trakt.IsConnected)
        {
            var watchlist = _db.GetTraktWatchlist();
            IsInWatchlist = watchlist.Any(w =>
                (!string.IsNullOrEmpty(item.TmdbId) && w.TmdbId == item.TmdbId) ||
                (!string.IsNullOrEmpty(item.ImdbId) && w.ImdbId == item.ImdbId) ||
                w.Title.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
        }

        // Non-blocking background trailer detection
        StartBackgroundTrailerResolve(item, parentSeries, client);
    }

    private void StartBackgroundTrailerResolve(EmbyItem item, EmbyItem? parentSeries, EmbyClient client)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var srvUrl = _currentServer?.Url ?? client.BaseUrl;
                var srvToken = _currentServer?.AccessToken ?? client.Token;
                var trailerItem = item;
                if ((trailerItem.RemoteTrailers == null || trailerItem.RemoteTrailers.Count == 0) && parentSeries != null)
                {
                    trailerItem = parentSeries;
                }
                var trailerUrl = await _trailerService.ResolveTrailerStreamUrlAsync(trailerItem, client, srvUrl, srvToken);
                if (!string.IsNullOrEmpty(trailerUrl))
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (Item?.Id == item.Id)
                        {
                            BackgroundTrailerUrl = trailerUrl;
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("DetailViewModel", $"Trailer detection failed: {ex.Message}");
            }
        });
    }

    private async Task LoadEpisodesAsync(string seriesId, string seasonId)
    {
        var client = _currentClient ?? _embyClient;

        // 1. Instant Cached Episodes (0ms!)
        var cachedEps = _db.GetCachedChildren(seasonId, "Episode");
        if (cachedEps.Count > 0)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                Episodes.Clear();
                foreach (var ep in cachedEps)
                {
                    var tag = ep.ImageTags?.GetValueOrDefault("Primary");
                    ep.ServerId = _currentServer?.Id;
                    ep.ServerName = _currentServer?.Name;
                    ep.PrimaryImageUrl = client.BuildImageUrl(ep.Id, "Primary", 400, tag, _currentServer?.Url, _currentServer?.AccessToken);
                    ep.SeriesImdbId = Item?.ImdbId;
                    Episodes.Add(ep);
                }
                OnPropertyChanged(nameof(Episodes));
                OnPropertyChanged(nameof(HasEpisodes));
            });
        }

        // 2. Background Refresh from Emby Server
        _ = Task.Run(async () =>
        {
            try
            {
                var eps = await client.GetEpisodesAsync(seriesId, seasonId);
                if (eps != null && eps.Count > 0)
                {
                    _db.UpsertItems(eps, _currentServer?.Id ?? "server", seasonId);

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (SelectedSeason?.Id == seasonId)
                        {
                            Episodes.Clear();
                            foreach (var ep in eps)
                            {
                                var tag = ep.ImageTags?.GetValueOrDefault("Primary");
                                ep.ServerId = _currentServer?.Id;
                                ep.ServerName = _currentServer?.Name;
                                ep.PrimaryImageUrl = client.BuildImageUrl(ep.Id, "Primary", 400, tag, _currentServer?.Url, _currentServer?.AccessToken);
                                ep.SeriesImdbId = Item?.ImdbId;
                                Episodes.Add(ep);
                            }
                            OnPropertyChanged(nameof(Episodes));
                            OnPropertyChanged(nameof(HasEpisodes));
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DetailViewModel", $"Episodes background refresh failed for season {seasonId}", ex);
            }
        });
    }

    public static int GetQualityRank(EmbyItem item)
    {
        int rank = 0;
        var res = item.ResolutionBadge;
        if (res == "4K UHD" || item.Height >= 1440 || item.Width >= 2560) rank = 4000;
        else if (res == "1080p" || item.Height >= 900) rank = 3000;
        else if (res == "720p" || item.Height >= 600) rank = 2000;
        else rank = 1000;

        var ed = item.Edition;
        if (ed == "IMAX") rank += 300;
        else if (ed == "Remux") rank += 250;
        else if (ed == "BluRay") rank += 200;
        else if (ed == "WEB-DL") rank += 150;
        else if (ed == "WEBRip") rank += 100;

        var hdr = item.HdrType;
        if (hdr == "DV") rank += 50;
        else if (hdr.StartsWith("HDR")) rank += 40;

        return rank;
    }

    public event Action<EmbyItem, long>? PlayRequested;

    public async Task PlayAsync(EmbyItem? target = null)
    {
        var playItem = target ?? SelectedVersion ?? Item;
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
                var client = _currentClient ?? _embyClient;
                var seasons = Seasons.Count > 0 ? Seasons.ToList() : await client.GetSeasonsAsync(playItem.Id);
                var season = SelectedSeason ?? seasons.FirstOrDefault();
                if (season != null)
                {
                    var eps = await client.GetEpisodesAsync(playItem.Id, season.Id);
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

        if (string.IsNullOrEmpty(playItem.ServerId))
        {
            playItem.ServerId = _currentServer?.Id;
            playItem.ServerName = _currentServer?.Name;
        }

        var startTicks = playItem.UserData?.PlaybackPositionTicks ?? 0;

        if (PlayRequested != null)
        {
            PlayRequested.Invoke(playItem, startTicks);
            return;
        }

        var clientToUse = ResolveClient(playItem.ServerId);
        var srv = _currentServer;

        var mediaSourceId = playItem.MediaSources?.FirstOrDefault()?.Id ?? playItem.Id;
        var streamUrl = clientToUse.GetStreamUrl(playItem.Id, mediaSourceId, srv?.Url, srv?.AccessToken);

        // Pre-load skip segments if it's an episode and has IMDB ID
        if (playItem.Type == "Episode" && !string.IsNullOrEmpty(playItem.ImdbId))
        {
            _ = _introDb.LoadSegmentsForEpisodeAsync(
                playItem.ImdbId,
                playItem.ParentIndexNumber ?? 1,
                playItem.IndexNumber ?? 1);
        }

        await _mpv.PlayAsync(streamUrl, playItem.Name, startTicks);
        await clientToUse.ReportPlaybackStartAsync(playItem.Id, mediaSourceId, startTicks);
        await _trakt.ScrobbleStartAsync(playItem, playItem.ProgressPercentage);
        _db.RecordWatchEvent(playItem.Id, srv?.Url ?? clientToUse.BaseUrl, playItem.Name, playItem.Type, (int)((playItem.RunTimeTicks ?? 0) / 10_000_000), playItem.SeriesName, playItem.Genres);
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
        var client = ResolveClient(Item.ServerId);

        var success = newPlayed
            ? await client.MarkPlayedAsync(Item.Id)
            : await client.MarkUnplayedAsync(Item.Id);

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
        var client = ResolveClient(ep.ServerId);

        var success = newPlayed
            ? await client.MarkPlayedAsync(ep.Id)
            : await client.MarkUnplayedAsync(ep.Id);

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
        var client = ResolveClient(Item.ServerId);

        var success = await client.ToggleFavoriteAsync(Item.Id, newFav);
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
        var trailerUrl = trailer?.Url ?? BackgroundTrailerUrl;
        if (!string.IsNullOrWhiteSpace(trailerUrl))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = trailerUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DetailViewModel", $"Failed to launch trailer: {ex.Message}");
            }
        }
    }

    public bool IsWatchPartyActive => _watchParty.IsActive;
    public string? WatchPartyTunnelUrl => _watchParty.TunnelUrl ?? _watchParty.LocalUrl;
    public string WatchPartyStateText => _watchParty.State == WatchPartySessionState.Live ? "LIVE" : "WAITING FOR GUESTS";

    public bool IsWatchPartyModalOpen
    {
        get => _isWatchPartyModalOpen;
        set => SetProperty(ref _isWatchPartyModalOpen, value);
    }

    public bool IsWatchPartyDownloading
    {
        get => _isWatchPartyDownloading;
        set => SetProperty(ref _isWatchPartyDownloading, value);
    }

    public string WatchPartyStatusText
    {
        get => _watchPartyStatusText;
        set => SetProperty(ref _watchPartyStatusText, value);
    }

    public int WatchPartyProgress
    {
        get => _watchPartyProgress;
        set => SetProperty(ref _watchPartyProgress, value);
    }

    public string WatchPartyGuestsText
    {
        get => _watchPartyGuestsText;
        set => SetProperty(ref _watchPartyGuestsText, value);
    }

    public string WatchPartyTranscodedText
    {
        get => _watchPartyTranscodedText;
        set => SetProperty(ref _watchPartyTranscodedText, value);
    }

    public string SelectedQuality
    {
        get => _selectedQuality;
        set => SetProperty(ref _selectedQuality, value);
    }

    public bool StartFromResume
    {
        get => _startFromResume;
        set => SetProperty(ref _startFromResume, value);
    }

    public string CopyButtonText
    {
        get => _copyButtonText;
        set => SetProperty(ref _copyButtonText, value);
    }

    public List<string> AvailableQualities { get; } = new() { "1080p Full HD", "720p HD", "4K Ultra HD" };

    public async Task OpenWatchPartyAsync()
    {
        if (IsWatchPartyActive)
        {
            OnPropertyChanged(nameof(IsWatchPartyActive));
            return;
        }

        IsWatchPartyModalOpen = true;

        if (!_watchParty.AreBinariesReady())
        {
            IsWatchPartyDownloading = true;
            WatchPartyStatusText = "Downloading FFmpeg and Cloudflared binaries...";
            WatchPartyProgress = 0;

            var progress = new Progress<(string phase, int percent)>(data =>
            {
                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    WatchPartyStatusText = data.phase switch
                    {
                        "cloudflared" => $"Downloading cloudflared.exe ({data.percent}%)...",
                        "ffmpeg" => $"Downloading FFmpeg build ({data.percent}%)...",
                        "unzip" => "Extracting FFmpeg binaries...",
                        _ => $"Setting up {data.phase}..."
                    };
                    WatchPartyProgress = data.percent;
                });
            });

            try
            {
                var ok = await _watchParty.EnsureBinariesAsync(progress);
                if (ok)
                {
                    IsWatchPartyDownloading = false;
                }
                else
                {
                    WatchPartyStatusText = "Failed to download binaries. Please check your internet connection.";
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("DetailViewModel", "EnsureBinaries failed", ex);
                WatchPartyStatusText = $"Setup failed: {ex.Message}";
            }
        }
        else
        {
            IsWatchPartyDownloading = false;
        }
    }

    public void CloseWatchPartyModal()
    {
        IsWatchPartyModalOpen = false;
    }

    public async Task StartWatchPartySessionAsync()
    {
        var playItem = Item;
        if (playItem == null) return;

        if (playItem.Type == "Series" && Episodes.Count > 0)
        {
            playItem = Episodes.FirstOrDefault(e => !e.IsPlayed) ?? Episodes[0];
        }

        var playServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == playItem.ServerId) ?? _currentServer;
        var client = ResolveClient(playItem.ServerId);
        var mediaSourceId = playItem.MediaSources?.FirstOrDefault()?.Id ?? playItem.Id;
        var streamUrl = client.GetStreamUrl(playItem.Id, mediaSourceId, playServer?.Url, playServer?.AccessToken);
        var durationSec = (playItem.RunTimeTicks ?? 0) / 10_000_000.0;
        var startOffset = StartFromResume && (playItem.UserData?.PlaybackPositionTicks ?? 0) > 0
            ? (playItem.UserData!.PlaybackPositionTicks / 10_000_000.0)
            : 0;

        var targetHeight = SelectedQuality.StartsWith("720") ? 720 : SelectedQuality.Contains("4K") ? 2160 : 1080;

        IsWatchPartyModalOpen = false;

        try
        {
            await _watchParty.StartSessionAsync(playItem.Name, streamUrl, durationSec, startOffset, targetHeight);
            OnPropertyChanged(nameof(IsWatchPartyActive));
            OnPropertyChanged(nameof(WatchPartyTunnelUrl));
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("DetailViewModel", "StartWatchPartySessionAsync failed", ex);
        }
    }

    public async Task StopWatchPartySessionAsync()
    {
        await _watchParty.StopSessionAsync();
        OnPropertyChanged(nameof(IsWatchPartyActive));
    }

    public void CopyWatchPartyLink()
    {
        var url = WatchPartyTunnelUrl;
        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                Clipboard.SetText(url);
                CopyButtonText = "Copied! ✓";
                _ = Task.Delay(2500).ContinueWith(_ =>
                {
                    Application.Current?.Dispatcher?.Invoke(() => CopyButtonText = "Copy Link");
                });
            }
            catch { }
        }
    }

    public async Task StartWatchPartyShowAndPlayAsync()
    {
        _watchParty.MarkShowLive();
        await PlayAsync();
    }

    public void GoBack()
    {
        NavigateBack?.Invoke();
    }
}
