using System.Collections.ObjectModel;
using System.Windows;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class HomeViewModel : BaseViewModel
{
    private readonly EmbyClient _embyClient;
    private readonly MpvController _mpv;
    private readonly DatabaseService _db;
    private readonly TraktService _trakt;
    private readonly SettingsService _settings;
    private readonly DedupEngine _dedup;
    private readonly TrailerService _trailerService;
    private EmbyItem? _heroItem;
    private string? _heroBackdropUrl;
    private string? _heroTrailerUrl;
    private bool _isLoading;

    public ObservableCollection<EmbyItem> ContinueWatching { get; } = new();
    public ObservableCollection<EmbyItem> NextUp { get; } = new();
    public ObservableCollection<EmbyItem> LatestMovies { get; } = new();
    public ObservableCollection<EmbyItem> LatestEpisodes { get; } = new();

    public event Action<EmbyItem>? NavigateToDetail;

    public HomeViewModel(
        EmbyClient embyClient,
        MpvController mpv,
        DatabaseService db,
        TraktService trakt,
        SettingsService settings,
        DedupEngine dedup,
        TrailerService trailerService)
    {
        _embyClient = embyClient;
        _mpv = mpv;
        _db = db;
        _trakt = trakt;
        _settings = settings;
        _dedup = dedup;
        _trailerService = trailerService;
    }

    public EmbyItem? HeroItem
    {
        get => _heroItem;
        set => SetProperty(ref _heroItem, value);
    }

    public string? HeroBackdropUrl
    {
        get => _heroBackdropUrl;
        set => SetProperty(ref _heroBackdropUrl, value);
    }

    public string? HeroTrailerUrl
    {
        get => _heroTrailerUrl;
        set => SetProperty(ref _heroTrailerUrl, value);
    }

    // Spec §3.3 (A0): every shelf shows its dedup group's elected artwork, from the version's own server (Artwork).
    // Continue Watching cards are landscape, so they ask for a wider image.
    private void ApplyArtwork(List<EmbyItem> resume, List<EmbyItem> movies, List<EmbyItem> episodes, List<EmbyItem>? nextUp = null)
    {
        var servers = _settings.Current.Servers;
        Artwork.Apply(resume, _db, servers, 500);
        Artwork.Apply(movies, _db, servers);
        Artwork.Apply(episodes, _db, servers);
        if (nextUp != null) Artwork.Apply(nextUp, _db, servers);
    }

    private EmbyItem? PickHero()
    {
        var servers = _settings.Current.Servers;
        return ContinueWatching.FirstOrDefault(h => Artwork.Backdrop(h, servers, 1920) != null)
               ?? LatestMovies.FirstOrDefault(h => Artwork.Backdrop(h, servers, 1920) != null)
               ?? ContinueWatching.FirstOrDefault()
               ?? LatestMovies.FirstOrDefault();
    }

    private async Task ResolveHeroTrailerAsync(EmbyItem hero, string? srvUrl, string? token)
    {
        HeroTrailerUrl = null;
        try
        {
            var client = _embyClient;
            if (!string.IsNullOrEmpty(srvUrl) && !string.IsNullOrEmpty(token))
            {
                client = EmbyClient.CreateForServer(new ServerConfig { Url = srvUrl, AccessToken = token });
            }

            if (hero.RemoteTrailers == null || hero.RemoteTrailers.Count == 0)
            {
                var fullHero = await client.GetItemAsync(hero.Id);
                if (fullHero != null)
                {
                    hero.RemoteTrailers = fullHero.RemoteTrailers;
                    hero.OfficialRating ??= fullHero.OfficialRating;
                    hero.Taglines ??= fullHero.Taglines;
                }
            }

            var trailerPath = await _trailerService.ResolveTrailerStreamUrlAsync(hero, client, srvUrl, token);
            if (!string.IsNullOrEmpty(trailerPath))
            {
                HeroTrailerUrl = trailerPath;
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("HomeViewModel", $"Hero trailer resolution failed: {ex.Message}");
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(ShowSkeleton));
        }
    }

    public bool IsEmpty => !IsLoading && ContinueWatching.Count == 0 && NextUp.Count == 0 && LatestMovies.Count == 0 && LatestEpisodes.Count == 0;

    // Placeholder shelves while the first load is in flight and nothing is on screen yet.
    public bool ShowSkeleton => IsLoading && ContinueWatching.Count == 0 && NextUp.Count == 0 && LatestMovies.Count == 0 && LatestEpisodes.Count == 0;

    private Task? _loadInFlight;

    // Single-flight: HomePage.Loaded and MainViewModel.InitializeAsync both request the first load.
    public Task LoadAsync() => _loadInFlight is { IsCompleted: false } running ? running : (_loadInFlight = LoadCoreAsync());

    private async Task LoadCoreAsync()
    {
        if (!_embyClient.IsAuthenticated && _settings.Current.Servers.Count == 0) return;

        IsLoading = true;
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            // 1. Instant Cache-First Display (0 ms) from SQLite
            try
            {
                if (ContinueWatching.Count == 0 && LatestMovies.Count == 0 && LatestEpisodes.Count == 0)
                {
                    // Off the UI thread: on a large cache these reads take seconds and would freeze first paint.
                    var (cachedResume, cachedMovies, cachedEpisodes) = await Task.Run(() =>
                    {
                        var resume = _db.GetContinueWatchingItems();
                        if (resume.Count == 0) resume = _db.GetCachedContinueWatching();
                        var (movies, episodes) = (_db.GetCachedItems("Movie", limit: 20), _db.GetCachedItems("Episode", limit: 20));
                        ApplyArtwork(resume, movies, episodes);
                        return (resume, movies, episodes);
                    });

                    if (cachedResume.Count > 0 || cachedMovies.Count > 0 || cachedEpisodes.Count > 0)
                    {
                        var activeServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId)
                                           ?? _settings.Current.Servers.FirstOrDefault();

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var it in cachedResume)
                            {
                                var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == it.ServerId) ?? activeServer;
                                it.ServerId ??= srv?.Id;
                                it.ServerName ??= srv?.Name;
                                ContinueWatching.Add(it);
                            }

                            foreach (var m in cachedMovies)
                            {
                                var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == m.ServerId) ?? activeServer;
                                m.ServerId ??= srv?.Id;
                                m.ServerName ??= srv?.Name;
                                LatestMovies.Add(m);
                            }

                            foreach (var ep in cachedEpisodes)
                            {
                                var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == ep.ServerId) ?? activeServer;
                                ep.ServerId ??= srv?.Id;
                                ep.ServerName ??= srv?.Name;
                                LatestEpisodes.Add(ep);
                            }

                            if (HeroItem == null && PickHero() is { } heroCandidate)
                            {
                                HeroItem = heroCandidate;
                                HeroBackdropUrl = Artwork.Backdrop(heroCandidate, _settings.Current.Servers, 1920);
                            }
                            OnPropertyChanged(nameof(ShowSkeleton));
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("HomeViewModel", $"Cache read fallback: {ex.Message}");
            }

            var isCombined = _settings.Current.LibraryMode == "combined" && _settings.Current.Servers.Count > 1;

            if (isCombined)
            {
                // Multi-Server Combined Mode: Fan-out across ALL servers in parallel
                var servers = _settings.Current.Servers.ToList();
                var serverTasks = servers.Select(async srv =>
                {
                    var client = (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated)
                        ? _embyClient
                        : EmbyClient.CreateForServer(srv);

                    try
                    {
                        var resumeTask = client.GetResumeAsync();
                        var nextUpTask = client.GetNextUpAsync();
                        var moviesTask = client.GetItemsAsync(includeItemTypes: "Movie", limit: 24, sortBy: "DateCreated", sortOrder: "Descending", fields: EmbyClient.LightItemFields);
                        var episodesTask = client.GetItemsAsync(includeItemTypes: "Episode", limit: 24, sortBy: "DateCreated", sortOrder: "Descending", fields: EmbyClient.LightItemFields);

                        await Task.WhenAll(resumeTask, nextUpTask, moviesTask, episodesTask);

                        var resItems = await resumeTask;
                        var nxtItems = await nextUpTask;
                        var movItems = (await moviesTask).Items;
                        var epItems = (await episodesTask).Items;

                        foreach (var it in resItems) { it.ServerId = srv.Id; it.ServerName = srv.Name; }
                        foreach (var it in nxtItems) { it.ServerId = srv.Id; it.ServerName = srv.Name; }
                        foreach (var it in movItems) { it.ServerId = srv.Id; it.ServerName = srv.Name; }
                        foreach (var it in epItems) { it.ServerId = srv.Id; it.ServerName = srv.Name; }

                        return (resItems, nxtItems, movItems, epItems);
                    }
                    catch (Exception ex)
                    {
                        NocturneLogger.Warn("HomeViewModel", $"Failed to fetch home rows for server {srv.Name}: {ex.Message}");
                        return (new List<EmbyItem>(), new List<EmbyItem>(), new List<EmbyItem>(), new List<EmbyItem>());
                    }
                });

                var serverResults = await Task.WhenAll(serverTasks);

                var allResume = serverResults.SelectMany(r => r.Item1).ToList();
                var allNextUp = serverResults.SelectMany(r => r.Item2).ToList();
                var allMovies = serverResults.SelectMany(r => r.Item3).ToList();
                var allEpisodes = serverResults.SelectMany(r => r.Item4).ToList();

                foreach (var it in allResume)
                {
                    if (it.UserData != null && it.UserData.PlaybackPositionTicks > 0)
                    {
                        _db.UpdateLocalPlaybackProgress(it.ServerId ?? string.Empty, it.Id, it.UserData.PlaybackPositionTicks, it.UserData.Played, origin: Origin.Emby(it.ServerId ?? string.Empty)); // the server's resume point, not a local change
                    }
                }
                var dbResume = _db.GetContinueWatchingItems();
                var mergedResume = dbResume.Count > 0 ? dbResume : DeduplicateResume(allResume);
                var mergedNextUp = DeduplicateEpisodes(allNextUp);

                var dedupMovieGroups = _dedup.Deduplicate(allMovies);
                var mergedMovies = dedupMovieGroups.Select(g => g.PrimaryItem).Take(20).ToList();

                var mergedEpisodes = DeduplicateEpisodes(allEpisodes).Take(20).ToList();

                _db.SaveContinueWatchingCache(mergedResume);
                ApplyArtwork(mergedResume, mergedMovies, mergedEpisodes, mergedNextUp);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ContinueWatching.Clear();
                    foreach (var it in mergedResume) ContinueWatching.Add(it);

                    NextUp.Clear();
                    foreach (var it in mergedNextUp) NextUp.Add(it);

                    LatestMovies.Clear();
                    foreach (var it in mergedMovies) LatestMovies.Add(it);

                    LatestEpisodes.Clear();
                    foreach (var it in mergedEpisodes) LatestEpisodes.Add(it);

                    if (PickHero() is { } hero)
                    {
                        HeroItem = hero;
                        var heroServer = servers.FirstOrDefault(s => s.Id == hero.ServerId);
                        HeroBackdropUrl = Artwork.Backdrop(hero, servers, 1920);
                        _ = ResolveHeroTrailerAsync(hero, heroServer?.Url, heroServer?.AccessToken);
                    }

                    OnPropertyChanged(nameof(ContinueWatching));
                    OnPropertyChanged(nameof(NextUp));
                    OnPropertyChanged(nameof(LatestMovies));
                    OnPropertyChanged(nameof(LatestEpisodes));
                });
            }
            else
            {
                // Single Server Mode
                var activeServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
                var activeSid = activeServer?.Id ?? string.Empty;
                var activeSname = activeServer?.Name ?? string.Empty;

                var resumeTask = _embyClient.GetResumeAsync();
                var nextUpTask = _embyClient.GetNextUpAsync();
                var latestMoviesTask = _embyClient.GetItemsAsync(includeItemTypes: "Movie", limit: 20, sortBy: "DateCreated", sortOrder: "Descending", fields: EmbyClient.LightItemFields);
                var latestEpisodesTask = _embyClient.GetItemsAsync(includeItemTypes: "Episode", limit: 20, sortBy: "DateCreated", sortOrder: "Descending", fields: EmbyClient.LightItemFields);

                await Task.WhenAll(resumeTask, nextUpTask, latestMoviesTask, latestEpisodesTask);

                var resumeItems = await resumeTask;
                var nextUpItems = await nextUpTask;
                var movies = (await latestMoviesTask).Items;
                var eps = (await latestEpisodesTask).Items;

                foreach (var it in resumeItems)
                {
                    it.ServerId = activeSid;
                    it.ServerName = activeSname;
                    if (it.UserData != null && it.UserData.PlaybackPositionTicks > 0)
                    {
                        _db.UpdateLocalPlaybackProgress(activeSid, it.Id, it.UserData.PlaybackPositionTicks, it.UserData.Played, origin: Origin.Emby(activeSid)); // the server's resume point
                    }
                }
                foreach (var it in nextUpItems) { it.ServerId = activeSid; it.ServerName = activeSname; }
                foreach (var it in movies) { it.ServerId = activeSid; it.ServerName = activeSname; }
                foreach (var it in eps) { it.ServerId = activeSid; it.ServerName = activeSname; }

                var singleDbResume = _db.GetContinueWatchingItems();
                var displayResume = singleDbResume.Count > 0 ? singleDbResume : resumeItems;

                _db.SaveContinueWatchingCache(displayResume);
                ApplyArtwork(displayResume, movies, eps, nextUpItems);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    ContinueWatching.Clear();
                    foreach (var item in displayResume) ContinueWatching.Add(item);

                    NextUp.Clear();
                    foreach (var item in nextUpItems) NextUp.Add(item);

                    if (movies.Count > 0 || LatestMovies.Count == 0)
                    {
                        LatestMovies.Clear();
                        foreach (var m in movies) LatestMovies.Add(m);
                    }

                    if (eps.Count > 0 || LatestEpisodes.Count == 0)
                    {
                        LatestEpisodes.Clear();
                        foreach (var ep in eps) LatestEpisodes.Add(ep);
                    }

                    if (PickHero() is { } hero)
                    {
                        HeroItem = hero;
                        HeroBackdropUrl = Artwork.Backdrop(hero, _settings.Current.Servers, 1920);
                        _ = ResolveHeroTrailerAsync(hero, activeServer?.Url, activeServer?.AccessToken);
                    }

                    OnPropertyChanged(nameof(ContinueWatching));
                    OnPropertyChanged(nameof(NextUp));
                    OnPropertyChanged(nameof(LatestMovies));
                    OnPropertyChanged(nameof(LatestEpisodes));
                });
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("HomeViewModel", "Failed to load home rows", ex);
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    public async Task RemoveFromContinueWatchingAsync(EmbyItem item)
    {
        if (item == null) return;
        try
        {
            var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
                   ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
            var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
                ? _embyClient
                : EmbyClient.CreateForServer(srv);

            // Removing from Continue Watching clears the resume point; it is not a "watched" signal.
            await client.RemoveFromResumeAsync(item.Id);
            _db.ClearResumeProgress(item.Id, item.ServerId);

            if (FeatureFlags.SiblingCascade(_db) && long.TryParse(item.DedupGroupId, out var gid) && gid > 0)
            {
                foreach (var sib in _db.GetGroupVersions(gid.ToString()))
                {
                    _db.ClearResumeProgress(sib.Id, sib.ServerId);
                    if (!string.IsNullOrEmpty(sib.ServerId) && sib.ServerId != item.ServerId)
                    {
                        _db.EnqueueSiblingPlaybackWrite(sib.ServerId, sib.Id, 0, markPlayed: false, Origin.Local);
                    }
                }
            }

            _db.RemoveFromContinueWatchingCache(item.Id);

            Application.Current?.Dispatcher?.Invoke(() =>
            {
                ContinueWatching.Remove(item);
                OnPropertyChanged(nameof(ContinueWatching));
                OnPropertyChanged(nameof(IsEmpty));
            });
            NocturneLogger.Info("HomeViewModel", $"Removed '{item.Name}' from continue watching");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("HomeViewModel", $"Failed to remove '{item.Name}' from continue watching", ex);
        }
    }

    private static List<EmbyItem> DeduplicateResume(IEnumerable<EmbyItem> items)
    {
        var result = new List<EmbyItem>();
        foreach (var item in items)
        {
            var existing = result.FirstOrDefault(x =>
                (!string.IsNullOrEmpty(x.TmdbId) && !string.IsNullOrEmpty(item.TmdbId) && x.TmdbId == item.TmdbId) ||
                (!string.IsNullOrEmpty(x.ImdbId) && !string.IsNullOrEmpty(item.ImdbId) && x.ImdbId == item.ImdbId) ||
                (x.Type == "Episode" && item.Type == "Episode" &&
                 string.Equals(x.SeriesName, item.SeriesName, StringComparison.OrdinalIgnoreCase) &&
                 x.ParentIndexNumber == item.ParentIndexNumber &&
                 x.IndexNumber == item.IndexNumber) ||
                (string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase) && x.ProductionYear == item.ProductionYear));

            if (existing == null)
            {
                result.Add(item);
            }
            else
            {
                var existingDate = existing.UserData?.LastPlayedDate ?? DateTime.MinValue;
                var currentDate = item.UserData?.LastPlayedDate ?? DateTime.MinValue;
                if (currentDate >= existingDate)
                {
                    result.Remove(existing);
                    result.Add(item);
                }
            }
        }
        return result.OrderByDescending(x => x.UserData?.LastPlayedDate ?? DateTime.MinValue).ToList();
    }

    private static List<EmbyItem> DeduplicateEpisodes(IEnumerable<EmbyItem> items)
    {
        var result = new List<EmbyItem>();
        foreach (var item in items)
        {
            var match = result.FirstOrDefault(x =>
                (string.Equals(x.SeriesName, item.SeriesName, StringComparison.OrdinalIgnoreCase) ||
                 (!string.IsNullOrEmpty(x.SeriesImdbId) && x.SeriesImdbId == item.SeriesImdbId)) &&
                x.ParentIndexNumber == item.ParentIndexNumber &&
                x.IndexNumber == item.IndexNumber);

            if (match == null)
            {
                result.Add(item);
            }
        }
        return result;
    }

    public event Action<EmbyItem>? PlayRequested;

    public async Task PlayItemAsync(EmbyItem item)
    {
        if (PlayRequested != null)
        {
            PlayRequested.Invoke(item);
            return;
        }

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
