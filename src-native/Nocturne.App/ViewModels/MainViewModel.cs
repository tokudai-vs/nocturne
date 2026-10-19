using System.Windows;
using Wpf.Ui.Controls;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class MainViewModel : BaseViewModel
{
    private readonly SettingsService _settings;
    private readonly EmbyClient _embyClient;
    private readonly MpvController _mpv;
    private readonly IntroDbService _introDb;
    private readonly TraktService _trakt;
    private readonly DatabaseService _db;

    private BaseViewModel _currentViewModel;
    private WindowBackdropType _backdropType = WindowBackdropType.Mica;
    private string _searchQuery = string.Empty;
    private bool _isSplashVisible = true;
    private string _splashStatusText = "Initializing Nocturne...";
    private bool _isNavigationVisible = true;

    // Player State
    private bool _isStartingPlayback;
    private bool _isPlayerActive;
    private string _playerTitle = string.Empty;
    private string _playerSubtitle = string.Empty;
    private bool _isSkipPromptVisible;
    private string _skipPromptText = "Skip Intro";
    private double _skipTargetSeconds = 0;
    private bool _canGoNextEpisode;
    private bool _canGoPrevEpisode;
    private EmbyItem? _currentPlayingItem;
    private EmbyItem? _nextEpisodeItem;
    private EmbyItem? _prevEpisodeItem;
    private double _lastPlaybackSeconds = 0;
    private double _currentPlaybackSeconds = 0;
    private double _totalDurationSeconds = 0;
    private bool _isPaused;
    private double _volume = 100;
    private string _timeString = "00:00 / 00:00";

    public HomeViewModel HomeVM { get; }
    public LibraryViewModel LibraryVM { get; }
    public DetailViewModel DetailVM { get; }
    public SearchViewModel SearchVM { get; }
    public SettingsViewModel SettingsVM { get; }
    public AnalyticsViewModel AnalyticsVM { get; }
    public OnboardingViewModel OnboardingVM { get; }

    public event Func<EmbyItem, long, Task>? RequestEmbeddedPlayback;

    public MainViewModel(
        SettingsService settings,
        EmbyClient embyClient,
        MpvController mpv,
        IntroDbService introDb,
        TraktService trakt,
        DatabaseService db,
        HomeViewModel homeVM,
        LibraryViewModel libraryVM,
        DetailViewModel detailVM,
        SearchViewModel searchVM,
        SettingsViewModel settingsVM,
        AnalyticsViewModel analyticsVM,
        OnboardingViewModel onboardingVM)
    {
        _settings = settings;
        _embyClient = embyClient;
        _mpv = mpv;
        _introDb = introDb;
        _trakt = trakt;
        _db = db;

        HomeVM = homeVM;
        LibraryVM = libraryVM;
        DetailVM = detailVM;
        SearchVM = searchVM;
        SettingsVM = settingsVM;
        AnalyticsVM = analyticsVM;
        OnboardingVM = onboardingVM;

        _currentViewModel = HomeVM;

        // Wire navigation events
        HomeVM.NavigateToDetail += item => NavigateToDetail(item);
        LibraryVM.NavigateToDetail += item => NavigateToDetail(item);
        SearchVM.NavigateToDetail += item => NavigateToDetail(item);
        DetailVM.NavigateBack += () => CurrentViewModel = HomeVM;

        // Wire playback requests to embedded player
        HomeVM.PlayRequested += item => _ = StartPlaybackAsync(item, item.UserData?.PlaybackPositionTicks ?? 0);
        DetailVM.PlayRequested += (item, ticks) => _ = StartPlaybackAsync(item, ticks);

        // IntroDB skip prompt updates
        _introDb.PromptChanged += prompt =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (prompt != null)
                {
                    SkipPromptText = prompt.ActionLabel;
                    _skipTargetSeconds = prompt.TargetSeekSec;
                    IsSkipPromptVisible = true;
                }
                else
                {
                    IsSkipPromptVisible = false;
                }
            });
        };

        // MPV position & EOF handlers
        _mpv.PositionChanged += sec =>
        {
            _lastPlaybackSeconds = sec;
            _introDb.OnPositionChanged(sec, _mpv);
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                CurrentPlaybackSeconds = sec;
                UpdateTimeString();
            });
        };

        _mpv.DurationChanged += dur =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                TotalDurationSeconds = dur;
                UpdateTimeString();
            });
        };

        _mpv.PauseChanged += paused =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                IsPaused = paused;
            });
        };

        _mpv.VolumeChanged += vol =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                _volume = vol;
                OnPropertyChanged(nameof(Volume));
            });
        };

        _mpv.PlaybackEnded += () =>
        {
            if (_isStartingPlayback) return;
            Application.Current?.Dispatcher?.Invoke(async () =>
            {
                if (IsPlayerActive && !_isStartingPlayback)
                {
                    if (_settings.Current.AutoPlayNextEpisode && CanGoNextEpisode && _nextEpisodeItem != null)
                    {
                        NocturneLogger.Info("MainViewModel", $"Auto-advancing to next episode: {_nextEpisodeItem.Name}");
                        await PlayNextEpisodeAsync();
                    }
                    else
                    {
                        await StopPlaybackAsync();
                    }
                }
            });
        };

        // Wire onboarding completion
        OnboardingVM.Authenticated += async () =>
        {
            IsNavigationVisible = true;
            CurrentViewModel = HomeVM;
            await HomeVM.LoadAsync();
            await LibraryVM.LoadViewsAsync();
        };

        UpdateBackdropFromSettings();
        _settings.SettingsChanged += _ => UpdateBackdropFromSettings();
    }

    public BaseViewModel CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (SetProperty(ref _currentViewModel, value))
            {
                IsNavigationVisible = value is not OnboardingViewModel;
            }
        }
    }

    public WindowBackdropType BackdropType
    {
        get => _backdropType;
        set => SetProperty(ref _backdropType, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value) && !string.IsNullOrWhiteSpace(value))
            {
                SearchVM.Query = value;
                CurrentViewModel = SearchVM;
            }
        }
    }

    public bool IsSplashVisible
    {
        get => _isSplashVisible;
        set => SetProperty(ref _isSplashVisible, value);
    }

    public string SplashStatusText
    {
        get => _splashStatusText;
        set => SetProperty(ref _splashStatusText, value);
    }

    public bool IsNavigationVisible
    {
        get => _isNavigationVisible;
        set => SetProperty(ref _isNavigationVisible, value);
    }

    // Player Properties
    public bool IsPlayerActive
    {
        get => _isPlayerActive;
        set => SetProperty(ref _isPlayerActive, value);
    }

    public string PlayerTitle
    {
        get => _playerTitle;
        set => SetProperty(ref _playerTitle, value);
    }

    public string PlayerSubtitle
    {
        get => _playerSubtitle;
        set => SetProperty(ref _playerSubtitle, value);
    }

    public bool IsSkipPromptVisible
    {
        get => _isSkipPromptVisible;
        set => SetProperty(ref _isSkipPromptVisible, value);
    }

    public string SkipPromptText
    {
        get => _skipPromptText;
        set => SetProperty(ref _skipPromptText, value);
    }

    public bool CanGoNextEpisode
    {
        get => _canGoNextEpisode;
        set => SetProperty(ref _canGoNextEpisode, value);
    }

    public bool CanGoPrevEpisode
    {
        get => _canGoPrevEpisode;
        set => SetProperty(ref _canGoPrevEpisode, value);
    }

    public double CurrentPlaybackSeconds
    {
        get => _currentPlaybackSeconds;
        set => SetProperty(ref _currentPlaybackSeconds, value);
    }

    public double TotalDurationSeconds
    {
        get => _totalDurationSeconds;
        set => SetProperty(ref _totalDurationSeconds, value);
    }

    public bool IsPaused
    {
        get => _isPaused;
        set => SetProperty(ref _isPaused, value);
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, value))
            {
                _ = _mpv.SetVolumeAsync(value);
            }
        }
    }

    public string TimeString
    {
        get => _timeString;
        set => SetProperty(ref _timeString, value);
    }

    private void UpdateTimeString()
    {
        var cur = TimeSpan.FromSeconds(Math.Max(0, _currentPlaybackSeconds));
        var tot = TimeSpan.FromSeconds(Math.Max(0, _totalDurationSeconds));
        var curStr = tot.TotalHours >= 1 ? cur.ToString(@"hh\:mm\:ss") : cur.ToString(@"mm\:ss");
        var totStr = tot.TotalHours >= 1 ? tot.ToString(@"hh\:mm\:ss") : tot.ToString(@"mm\:ss");
        TimeString = $"{curStr} / {totStr}";
    }

    public async Task TogglePauseAsync() => await _mpv.TogglePauseAsync();
    public async Task SeekToAsync(double seconds) => await _mpv.SeekTo(seconds);
    public async Task SeekRelativeAsync(double delta) => await _mpv.SeekRelative(delta);

    public void NavigateToHome()
    {
        CurrentViewModel = HomeVM;
        if (HomeVM.ContinueWatching.Count == 0 && HomeVM.LatestMovies.Count == 0)
        {
            _ = HomeVM.LoadAsync();
        }
    }

    public void NavigateToLibrary()
    {
        CurrentViewModel = LibraryVM;
        if (LibraryVM.Views.Count == 0)
        {
            _ = LibraryVM.LoadViewsAsync();
        }
    }

    public void NavigateToSettings() => CurrentViewModel = SettingsVM;
    public void NavigateToAnalytics() => CurrentViewModel = AnalyticsVM;

    public void NavigateToDetail(EmbyItem item)
    {
        _ = DetailVM.LoadItemAsync(item.Id);
        CurrentViewModel = DetailVM;
    }

    private void UpdateBackdropFromSettings()
    {
        BackdropType = _settings.Current.BackdropMaterial switch
        {
            "Acrylic" => WindowBackdropType.Acrylic,
            "Tabbed" => WindowBackdropType.Tabbed,
            "None" => WindowBackdropType.None,
            _ => WindowBackdropType.Mica
        };
    }

    public async Task InitializeAsync()
    {
        SplashStatusText = "Connecting to database & checking servers...";

        var activeServer = _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId)
                           ?? _settings.Current.Servers.FirstOrDefault();

        if (activeServer != null && !string.IsNullOrEmpty(activeServer.AccessToken))
        {
            SplashStatusText = $"Connecting to {activeServer.Name}...";
            _embyClient.Configure(activeServer.Url, activeServer.AccessToken, activeServer.UserId);

            // Pre-load data while splash is displaying
            var loadHomeTask = HomeVM.LoadAsync();
            var loadLibTask = LibraryVM.LoadViewsAsync();

            // Give user the slick splash experience (min 1.2s)
            await Task.Delay(1200);
            await Task.WhenAll(loadHomeTask, loadLibTask);

            IsNavigationVisible = true;
            CurrentViewModel = HomeVM;
        }
        else
        {
            await Task.Delay(800);
            IsNavigationVisible = false;
            OnboardingVM.LoadSavedServers();
            CurrentViewModel = OnboardingVM;
        }

        IsSplashVisible = false;
    }

    public async Task StartPlaybackAsync(EmbyItem item, long startTicks = 0)
    {
        _currentPlayingItem = item;
        _nextEpisodeItem = null;
        _prevEpisodeItem = null;

        if (item.Type == "Episode")
        {
            PlayerTitle = !string.IsNullOrEmpty(item.SeriesName)
                ? $"{item.SeriesName} - S{item.ParentIndexNumber:D2}E{item.IndexNumber:D2}"
                : item.Name;
            PlayerSubtitle = item.Name;

            // Check if current episode is in DetailVM.Episodes
            if (DetailVM.Episodes.Count > 0)
            {
                var epList = DetailVM.Episodes.ToList();
                var idx = epList.FindIndex(e => e.Id == item.Id);
                if (idx >= 0)
                {
                    if (idx > 0) _prevEpisodeItem = epList[idx - 1];
                    if (idx < epList.Count - 1) _nextEpisodeItem = epList[idx + 1];
                }
            }

            // Fallback: If not found, asynchronously query series episodes
            if (_nextEpisodeItem == null && !string.IsNullOrEmpty(item.SeriesId))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var eps = await _embyClient.GetEpisodesAsync(item.SeriesId, item.SeasonId ?? "");
                        var idx = eps.FindIndex(e => e.Id == item.Id);
                        if (idx >= 0)
                        {
                            Application.Current?.Dispatcher?.Invoke(() =>
                            {
                                if (idx > 0) _prevEpisodeItem = eps[idx - 1];
                                if (idx < eps.Count - 1) _nextEpisodeItem = eps[idx + 1];
                                CanGoNextEpisode = _nextEpisodeItem != null;
                                CanGoPrevEpisode = _prevEpisodeItem != null;
                            });
                        }
                    }
                    catch { }
                });
            }
        }
        else
        {
            PlayerTitle = item.Name;
            PlayerSubtitle = item.ProductionYear?.ToString() ?? string.Empty;
        }

        CanGoNextEpisode = _nextEpisodeItem != null;
        CanGoPrevEpisode = _prevEpisodeItem != null;
        IsPlayerActive = true;
        _isStartingPlayback = true;

        try
        {
            if (RequestEmbeddedPlayback != null)
            {
                await RequestEmbeddedPlayback.Invoke(item, startTicks);
            }
        }
        finally
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1500);
                _isStartingPlayback = false;
            });
        }
    }

    public async Task ExecuteEmbeddedPlaybackAsync(IntPtr hwnd, EmbyItem item, long startTicks)
    {
        _currentPlayingItem = item;
        var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
        var streamUrl = _embyClient.GetStreamUrl(item.Id, mediaSourceId);

        NocturneLogger.Info("MainViewModel", $"Starting embedded mpv: {item.Name} ({item.Id}) at tick {startTicks} in HWND {hwnd}");
        await _mpv.PlayEmbeddedAsync(hwnd, streamUrl, item.Name, startTicks);
        await _embyClient.ReportPlaybackStartAsync(item.Id, mediaSourceId, startTicks);
        await _trakt.ScrobbleStartAsync(item, item.ProgressPercentage);
        _db.RecordWatchEvent(item.Id, _embyClient.BaseUrl, item.Name, item.Type, (int)((item.RunTimeTicks ?? 0) / 10_000_000), item.SeriesName, item.Genres);

        if (item.Type == "Episode")
        {
            var imdbId = item.SeriesImdbId ?? item.ImdbId ?? DetailVM.Item?.ImdbId ?? string.Empty;
            _ = _introDb.LoadSegmentsForEpisodeAsync(imdbId, item.ParentIndexNumber ?? 1, item.IndexNumber ?? 1);
        }
        else
        {
            _introDb.Clear();
        }
    }

    public async Task PlayNextEpisodeAsync()
    {
        if (_nextEpisodeItem != null)
        {
            var next = _nextEpisodeItem;
            await StartPlaybackAsync(next, 0);
        }
    }

    public async Task PlayPrevEpisodeAsync()
    {
        if (_prevEpisodeItem != null)
        {
            var prev = _prevEpisodeItem;
            await StartPlaybackAsync(prev, 0);
        }
    }

    public void ExecuteSkipPrompt()
    {
        if (IsSkipPromptVisible && _skipTargetSeconds > 0)
        {
            _ = _mpv.SeekTo(_skipTargetSeconds);
            IsSkipPromptVisible = false;
        }
    }

    public async Task StopPlaybackAsync()
    {
        if (!IsPlayerActive && !_mpv.IsPlaying) return;

        IsPlayerActive = false;
        _introDb.Clear();
        IsSkipPromptVisible = false;

        if (_currentPlayingItem != null)
        {
            var item = _currentPlayingItem;
            _currentPlayingItem = null;
            var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
            var positionTicks = (long)(_lastPlaybackSeconds * 10_000_000.0);
            var progress = item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
                ? Math.Clamp((double)positionTicks / item.RunTimeTicks.Value * 100.0, 0, 100)
                : 0;

            try
            {
                await _trakt.ScrobbleStopAsync(item, progress);
            }
            catch { }

            try
            {
                await _embyClient.ReportPlaybackStoppedAsync(item.Id, mediaSourceId, positionTicks);
            }
            catch { }

            try
            {
                _db.RecordWatchEvent(item.Id, _embyClient.BaseUrl, item.Name, item.Type, (int)_lastPlaybackSeconds, item.SeriesName, item.Genres);
            }
            catch { }
        }

        await _mpv.StopAsync();
    }
}
