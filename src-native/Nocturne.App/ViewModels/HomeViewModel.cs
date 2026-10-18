using System.Collections.ObjectModel;
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

    public async Task LoadAsync()
    {
        if (!_embyClient.IsAuthenticated) return;

        IsLoading = true;
        try
        {
            var resumeTask = _embyClient.GetResumeAsync();
            var nextUpTask = _embyClient.GetNextUpAsync();
            var latestMoviesTask = _embyClient.GetItemsAsync(includeItemTypes: "Movie", limit: 16, sortBy: "DateCreated", sortOrder: "Descending");
            var latestEpisodesTask = _embyClient.GetItemsAsync(includeItemTypes: "Episode", limit: 16, sortBy: "DateCreated", sortOrder: "Descending");

            await Task.WhenAll(resumeTask, nextUpTask, latestMoviesTask, latestEpisodesTask);

            // Continue Watching
            ContinueWatching.Clear();
            var resumeItems = await resumeTask;
            foreach (var item in resumeItems) ContinueWatching.Add(item);

            // Next Up
            NextUp.Clear();
            var nextUpItems = await nextUpTask;
            foreach (var item in nextUpItems) NextUp.Add(item);

            // Latest Movies
            LatestMovies.Clear();
            var movies = (await latestMoviesTask).Items;
            foreach (var m in movies) LatestMovies.Add(m);

            // Latest Episodes
            LatestEpisodes.Clear();
            var eps = (await latestEpisodesTask).Items;
            foreach (var ep in eps) LatestEpisodes.Add(ep);

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
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("HomeViewModel", "Failed to load home rows", ex);
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
