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
    private EmbyItem? _heroItem;
    private string? _heroBackdropUrl;
    private bool _isLoading;

    public ObservableCollection<EmbyItem> ContinueWatching { get; } = new();
    public ObservableCollection<EmbyItem> NextUp { get; } = new();
    public ObservableCollection<EmbyItem> LatestMovies { get; } = new();
    public ObservableCollection<EmbyItem> LatestEpisodes { get; } = new();

    public event Action<EmbyItem>? NavigateToDetail;

    public HomeViewModel(EmbyClient embyClient, MpvController mpv, DatabaseService db, TraktService trakt)
    {
        _embyClient = embyClient;
        _mpv = mpv;
        _db = db;
        _trakt = trakt;
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

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsEmpty => !IsLoading && ContinueWatching.Count == 0 && NextUp.Count == 0 && LatestMovies.Count == 0 && LatestEpisodes.Count == 0;

    public async Task LoadAsync()
    {
        if (!_embyClient.IsAuthenticated) return;

        IsLoading = true;
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            // 1. Instant Cache-First Display (0 ms)
            try
            {
                if (LatestMovies.Count == 0 && LatestEpisodes.Count == 0)
                {
                    var cachedMovies = _db.GetCachedItems("Movie", limit: 16);
                    var cachedEpisodes = _db.GetCachedItems("Episode", limit: 16);
                    if (cachedMovies.Count > 0 || cachedEpisodes.Count > 0)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            foreach (var m in cachedMovies) LatestMovies.Add(m);
                            foreach (var ep in cachedEpisodes) LatestEpisodes.Add(ep);

                            if (HeroItem == null)
                            {
                                var heroCandidate = LatestMovies.FirstOrDefault(h => h.BackdropImageTags?.Count > 0)
                                                 ?? LatestMovies.FirstOrDefault();
                                if (heroCandidate != null)
                                {
                                    HeroItem = heroCandidate;
                                    var tag = heroCandidate.BackdropImageTags?.FirstOrDefault() ?? heroCandidate.ImageTags?.GetValueOrDefault("Primary");
                                    HeroBackdropUrl = _embyClient.BuildImageUrl(heroCandidate.Id, "Backdrop", 1920, tag);
                                }
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("HomeViewModel", $"Cache read fallback: {ex.Message}");
            }

            // 2. Fast Emby API fetch in parallel using LightItemFields
            var resumeTask = _embyClient.GetResumeAsync();
            var nextUpTask = _embyClient.GetNextUpAsync();
            var latestMoviesTask = _embyClient.GetItemsAsync(includeItemTypes: "Movie", limit: 16, sortBy: "DateCreated", sortOrder: "Descending", fields: EmbyClient.LightItemFields);
            var latestEpisodesTask = _embyClient.GetItemsAsync(includeItemTypes: "Episode", limit: 16, sortBy: "DateCreated", sortOrder: "Descending", fields: EmbyClient.LightItemFields);

            await Task.WhenAll(resumeTask, nextUpTask, latestMoviesTask, latestEpisodesTask);

            var resumeItems = await resumeTask;
            var nextUpItems = await nextUpTask;
            var movies = (await latestMoviesTask).Items;
            var eps = (await latestEpisodesTask).Items;

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Continue Watching
                ContinueWatching.Clear();
                foreach (var item in resumeItems) ContinueWatching.Add(item);

                // Next Up
                NextUp.Clear();
                foreach (var item in nextUpItems) NextUp.Add(item);

                // Latest Movies
                if (movies.Count > 0 || LatestMovies.Count == 0)
                {
                    LatestMovies.Clear();
                    foreach (var m in movies) LatestMovies.Add(m);
                }

                // Latest Episodes
                if (eps.Count > 0 || LatestEpisodes.Count == 0)
                {
                    LatestEpisodes.Clear();
                    foreach (var ep in eps) LatestEpisodes.Add(ep);
                }

                // Select Hero item (prefer Continue Watching, fallback to Latest Movies)
                var hero = ContinueWatching.FirstOrDefault(h => h.BackdropImageTags?.Count > 0)
                        ?? LatestMovies.FirstOrDefault(h => h.BackdropImageTags?.Count > 0)
                        ?? ContinueWatching.FirstOrDefault()
                        ?? LatestMovies.FirstOrDefault();

                if (hero != null)
                {
                    HeroItem = hero;
                    var tag = hero.BackdropImageTags?.FirstOrDefault() ?? hero.ImageTags?.GetValueOrDefault("Primary");
                    HeroBackdropUrl = _embyClient.BuildImageUrl(hero.Id, "Backdrop", 1920, tag);
                }

                OnPropertyChanged(nameof(ContinueWatching));
                OnPropertyChanged(nameof(NextUp));
                OnPropertyChanged(nameof(LatestMovies));
                OnPropertyChanged(nameof(LatestEpisodes));
            });
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

    public event Action<EmbyItem>? PlayRequested;

    public async Task PlayItemAsync(EmbyItem item)
    {
        if (PlayRequested != null)
        {
            PlayRequested.Invoke(item);
            return;
        }

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
