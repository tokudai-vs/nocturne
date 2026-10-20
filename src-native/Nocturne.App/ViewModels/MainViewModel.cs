using System.Windows;
using System.Windows.Media;
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
    private readonly WatchPartyService _watchParty;
    private readonly SyncEngine _sync;

    private BaseViewModel _currentViewModel;
    private WindowBackdropType _backdropType = WindowBackdropType.Mica;
    private string _searchQuery = string.Empty;
    private bool _isSplashVisible = true;
    private string _splashStatusText = "Initializing Nocturne...";
    private bool _isNavigationVisible = true;
    private bool _isSyncing;
    private string _headerSyncStatusText = string.Empty;

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

    public IntroDbService IntroDb => _introDb;
    public event Action? ChaptersUpdated;
    public event Func<EmbyItem, long, Task>? RequestEmbeddedPlayback;

    public MainViewModel(
        SettingsService settings,
        EmbyClient embyClient,
        MpvController mpv,
        IntroDbService introDb,
        TraktService trakt,
        DatabaseService db,
        WatchPartyService watchParty,
        SyncEngine sync,
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
        _watchParty = watchParty;
        _sync = sync;

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

        // Sync and Cache refresh handlers
        _sync.ProgressReported += report =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                if (report.State == SyncState.Done || report.State == SyncState.Idle || report.State == SyncState.Error)
                {
                    IsSyncing = false;
                    SyncProgressPercent = 0;
                }
                else
                {
                    IsSyncing = true;
                    SyncProgressPercent = Math.Clamp(report.Percentage, 0, 100);
                    HeaderSyncStatusText = $"{report.StatusMessage} ({report.Percentage:F0}%)";
                }
            });
        };

        _sync.SyncCompleted += () =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                IsSyncing = false;
                _ = HomeVM.LoadAsync();
                _ = LibraryVM.LoadViewsAsync();
            });
        };

        SettingsVM.CacheCleared += () =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                _ = HomeVM.LoadAsync();
                _ = LibraryVM.LoadViewsAsync();
            });
        };

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

        _introDb.SegmentsLoaded += () =>
        {
            Application.Current?.Dispatcher?.Invoke(() => ChaptersUpdated?.Invoke());
        };

        // MPV position & EOF handlers
        _mpv.PositionChanged += sec =>
        {
            _lastPlaybackSeconds = sec;
            _introDb.OnPositionChanged(sec, _mpv);
            if (_watchParty.IsActive)
            {
                _watchParty.BroadcastHeartbeat(sec, _mpv.IsPlaying && !_mpv.IsPaused);
            }
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
            if (_watchParty.IsActive)
            {
                if (paused) _watchParty.BroadcastPause(_mpv.CurrentPositionSeconds);
                else _watchParty.BroadcastPlay(_mpv.CurrentPositionSeconds);
            }
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

        _mpv.SubtitleTrackChanged += trackId =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                _currentSubtitleTrackId = trackId;
                OnPropertyChanged(nameof(CurrentSubtitleTrackId));
            });
        };

        _mpv.AudioTrackChanged += trackId =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                _currentAudioTrackId = trackId;
                OnPropertyChanged(nameof(CurrentAudioTrackId));
            });
        };

        _mpv.PlaybackEnded += () =>
        {
            if (_isStartingPlayback) return;
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;

            app.Dispatcher.InvokeAsync(async () =>
            {
                try
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
                            await StopPlaybackAsync(isEof: true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("MainViewModel", $"PlaybackEnded error: {ex.Message}");
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

    private string _currentUserName = "User";
    private string _currentServerName = "Emby Server";
    private string _currentUserInitial = "U";

    public BaseViewModel CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (SetProperty(ref _currentViewModel, value))
            {
                IsNavigationVisible = value is not OnboardingViewModel;
                OnPropertyChanged(nameof(IsHomeActive));
                OnPropertyChanged(nameof(IsLibraryActive));
                OnPropertyChanged(nameof(IsDetailActive));
                OnPropertyChanged(nameof(IsSearchActive));
                OnPropertyChanged(nameof(IsSettingsActive));
                OnPropertyChanged(nameof(IsAnalyticsActive));
                OnPropertyChanged(nameof(IsOnboardingActive));
                OnPropertyChanged(nameof(IsHeroPage));
                OnPropertyChanged(nameof(TopBarForeground));
                OnPropertyChanged(nameof(TopBarSecondaryForeground));
                OnPropertyChanged(nameof(HomeTabIconBrush));
                OnPropertyChanged(nameof(LibraryTabIconBrush));
            }
        }
    }

    public bool IsHomeActive => CurrentViewModel is HomeViewModel;
    public bool IsLibraryActive => CurrentViewModel is LibraryViewModel;
    public bool IsDetailActive => CurrentViewModel is DetailViewModel;
    public bool IsSearchActive => CurrentViewModel is SearchViewModel;
    public bool IsSettingsActive => CurrentViewModel is SettingsViewModel;
    public bool IsAnalyticsActive => CurrentViewModel is AnalyticsViewModel;
    public bool IsOnboardingActive => CurrentViewModel is OnboardingViewModel;
    public bool IsHeroPage => CurrentViewModel is HomeViewModel || CurrentViewModel is DetailViewModel;

    public Brush TopBarForeground => IsHeroPage
        ? Brushes.White
        : (Brush)(Application.Current?.TryFindResource("TextFillColorPrimaryBrush") ?? Brushes.Black);

    public Brush TopBarSecondaryForeground => IsHeroPage
        ? new SolidColorBrush(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF))
        : (Brush)(Application.Current?.TryFindResource("TextFillColorSecondaryBrush") ?? Brushes.Gray);

    public Brush HomeTabIconBrush => IsHomeActive
        ? (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange)
        : TopBarSecondaryForeground;

    public Brush LibraryTabIconBrush => IsLibraryActive
        ? (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange)
        : TopBarSecondaryForeground;

    public string CurrentUserName
    {
        get => _currentUserName;
        set => SetProperty(ref _currentUserName, value);
    }

    public string CurrentServerName
    {
        get => _currentServerName;
        set => SetProperty(ref _currentServerName, value);
    }

    public string CurrentUserInitial
    {
        get => _currentUserInitial;
        set => SetProperty(ref _currentUserInitial, value);
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

    public bool IsSyncing
    {
        get => _isSyncing;
        set => SetProperty(ref _isSyncing, value);
    }

    public string HeaderSyncStatusText
    {
        get => _headerSyncStatusText;
        set => SetProperty(ref _headerSyncStatusText, value);
    }

    private double _syncProgressPercent;
    public double SyncProgressPercent
    {
        get => _syncProgressPercent;
        set => SetProperty(ref _syncProgressPercent, value);
    }

    public bool IsSwitchServerVisible => _settings.Current.LibraryMode != "combined" && _settings.Current.Servers.Count > 1;

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

    public EmbyItem? CurrentPlayingItem => _currentPlayingItem;
    public EmbyItem? CurrentItem => _currentPlayingItem;

    public List<MediaStreamInfo> AvailableAudioStreams =>
        _currentPlayingItem?.MediaSources?.FirstOrDefault()?.MediaStreams?
            .Where(s => s.Type?.Equals("Audio", StringComparison.OrdinalIgnoreCase) == true)
            .ToList() ?? new();

    public List<MediaStreamInfo> AvailableSubtitleStreams =>
        _currentPlayingItem?.MediaSources?.FirstOrDefault()?.MediaStreams?
            .Where(s => s.Type?.Equals("Subtitle", StringComparison.OrdinalIgnoreCase) == true)
            .ToList() ?? new();

    private int? _currentSubtitleTrackId;
    private int? _currentAudioTrackId;

    public int? CurrentSubtitleTrackId => _currentSubtitleTrackId;
    public int? CurrentAudioTrackId => _currentAudioTrackId;

    public async Task CycleSubtitleAsync()
    {
        await _mpv.CycleSubtitles();
    }

    public async Task CycleAudioAsync()
    {
        await _mpv.CycleAudio();
    }

    public string GetMediaKey(EmbyItem item)
    {
        if (item.Type == "Episode" && !string.IsNullOrEmpty(item.SeriesId))
            return $"series:{item.SeriesId}";
        return $"item:{item.Id}";
    }

    public async Task SetSubtitleTrackAsync(int trackId)
    {
        _currentSubtitleTrackId = trackId;
        OnPropertyChanged(nameof(CurrentSubtitleTrackId));
        await _mpv.SetSubtitleTrack(trackId);

        if (_currentPlayingItem != null)
        {
            var key = GetMediaKey(_currentPlayingItem);
            var stream = trackId > 0 ? AvailableSubtitleStreams.ElementAtOrDefault(trackId - 1) : null;
            _db.SaveMediaStreamPreference(key, null, null, trackId, stream?.Language ?? (trackId == 0 ? "off" : null));
        }
    }

    public async Task SetAudioTrackAsync(int trackId)
    {
        _currentAudioTrackId = trackId;
        OnPropertyChanged(nameof(CurrentAudioTrackId));
        await _mpv.SetAudioTrack(trackId);

        if (_currentPlayingItem != null)
        {
            var key = GetMediaKey(_currentPlayingItem);
            var stream = AvailableAudioStreams.ElementAtOrDefault(trackId - 1);
            _db.SaveMediaStreamPreference(key, trackId, stream?.Language, null, null);
        }
    }

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
        _ = DetailVM.LoadItemAsync(item.Id, item.ServerId, item);
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
            CurrentUserName = !string.IsNullOrEmpty(activeServer.Username) ? activeServer.Username : "User";
            CurrentServerName = _settings.Current.LibraryMode == "combined"
                ? $"Combined ({_settings.Current.Servers.Count} servers)"
                : (!string.IsNullOrEmpty(activeServer.Name) ? activeServer.Name : "Emby Server");
            CurrentUserInitial = CurrentUserName.Length > 0 ? CurrentUserName[0].ToString().ToUpperInvariant() : "U";

            SplashStatusText = _settings.Current.LibraryMode == "combined"
                ? $"Connecting to {_settings.Current.Servers.Count} servers..."
                : $"Connecting to {activeServer.Name}...";
            _embyClient.Configure(activeServer.Url, activeServer.AccessToken, activeServer.UserId);

            IsNavigationVisible = true;
            CurrentViewModel = HomeVM;

            // Kick off background loading immediately (cache renders in 0ms, network fetches in background)
            _ = HomeVM.LoadAsync();
            _ = LibraryVM.LoadViewsAsync();

            // Very brief 80ms yield to allow the initial window frame and controls to lay out, then dismiss splash
            await Task.Delay(80);
            IsSplashVisible = false;

            if (_settings.Current.SyncOnStartup)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2500); // Allow home screen to render smoothly first
                        await _sync.RunFullSyncAsync();
                    }
                    catch (Exception ex)
                    {
                        NocturneLogger.Warn("MainViewModel", $"Background startup sync error: {ex.Message}");
                    }
                });
            }
        }
        else
        {
            IsNavigationVisible = false;
            OnboardingVM.LoadSavedServers();
            CurrentViewModel = OnboardingVM;
            IsSplashVisible = false;
        }
    }

    public void Logout()
    {
        IsNavigationVisible = false;
        OnboardingVM.LoadSavedServers();
        CurrentViewModel = OnboardingVM;
    }

    public void SwitchServer()
    {
        IsNavigationVisible = false;
        OnboardingVM.LoadSavedServers();
        CurrentViewModel = OnboardingVM;
    }

    public async Task StartPlaybackAsync(EmbyItem item, long startTicks = 0)
    {
        if (startTicks <= 0 && item.UserData != null && item.UserData.PlaybackPositionTicks > 0)
        {
            startTicks = item.UserData.PlaybackPositionTicks;
        }

        _currentPlayingItem = item;
        _nextEpisodeItem = null;
        _prevEpisodeItem = null;

        _lastPlaybackSeconds = (double)startTicks / 10_000_000.0;
        CurrentPlaybackSeconds = _lastPlaybackSeconds;
        TotalDurationSeconds = (double)(item.RunTimeTicks ?? 0) / 10_000_000.0;
        UpdateTimeString();

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
                        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
                               ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
                        var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
                            ? _embyClient
                            : EmbyClient.CreateForServer(srv);

                        var eps = await client.GetEpisodesAsync(item.SeriesId, item.SeasonId ?? "");
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
        _currentSubtitleTrackId = null;
        _currentAudioTrackId = null;
        OnPropertyChanged(nameof(CurrentSubtitleTrackId));
        OnPropertyChanged(nameof(CurrentAudioTrackId));
        OnPropertyChanged(nameof(AvailableSubtitleStreams));
        OnPropertyChanged(nameof(AvailableAudioStreams));

        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
               ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
        var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
            ? _embyClient
            : EmbyClient.CreateForServer(srv);

        var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
        var streamUrl = client.GetStreamUrl(item.Id, mediaSourceId, srv?.Url, srv?.AccessToken);

        NocturneLogger.Info("MainViewModel", $"Starting embedded mpv: {item.Name} ({item.Id}) on server '{srv?.Name}' at tick {startTicks} in HWND {hwnd}");
        await _mpv.PlayEmbeddedAsync(
            hwnd,
            streamUrl,
            item.Name,
            startTicks,
            subFont: _settings.Current.SubtitleFont,
            subSize: _settings.Current.SubtitleSize,
            subColor: _settings.Current.SubtitleColor,
            subBorder: _settings.Current.SubtitleBorder,
            subBg: _settings.Current.SubtitleBackground);

        // Restore user track preferences for series / movie
        var mediaKey = GetMediaKey(item);
        var pref = _db.GetMediaStreamPreference(mediaKey);
        if (pref != null)
        {
            // Apply audio track preference
            var audioStreams = AvailableAudioStreams;
            int matchedAudio = -1;
            if (!string.IsNullOrEmpty(pref.Value.AudioLanguage))
            {
                matchedAudio = audioStreams.FindIndex(s => string.Equals(s.Language, pref.Value.AudioLanguage, StringComparison.OrdinalIgnoreCase));
            }
            if (matchedAudio < 0 && pref.Value.AudioTrackId.HasValue && pref.Value.AudioTrackId.Value > 0 && pref.Value.AudioTrackId.Value <= audioStreams.Count)
            {
                matchedAudio = pref.Value.AudioTrackId.Value - 1;
            }
            if (matchedAudio >= 0)
            {
                _currentAudioTrackId = matchedAudio + 1;
                OnPropertyChanged(nameof(CurrentAudioTrackId));
                _ = _mpv.SetAudioTrack(_currentAudioTrackId.Value);
            }

            // Apply subtitle track preference
            var subStreams = AvailableSubtitleStreams;
            if (pref.Value.SubtitleTrackId == 0 || pref.Value.SubtitleLanguage == "off")
            {
                _currentSubtitleTrackId = 0;
                OnPropertyChanged(nameof(CurrentSubtitleTrackId));
                _ = _mpv.SetSubtitleTrack(0);
            }
            else
            {
                int matchedSub = -1;
                if (!string.IsNullOrEmpty(pref.Value.SubtitleLanguage))
                {
                    matchedSub = subStreams.FindIndex(s => string.Equals(s.Language, pref.Value.SubtitleLanguage, StringComparison.OrdinalIgnoreCase));
                }
                if (matchedSub < 0 && pref.Value.SubtitleTrackId.HasValue && pref.Value.SubtitleTrackId.Value > 0 && pref.Value.SubtitleTrackId.Value <= subStreams.Count)
                {
                    matchedSub = pref.Value.SubtitleTrackId.Value - 1;
                }
                if (matchedSub >= 0)
                {
                    _currentSubtitleTrackId = matchedSub + 1;
                    OnPropertyChanged(nameof(CurrentSubtitleTrackId));
                    _ = _mpv.SetSubtitleTrack(_currentSubtitleTrackId.Value);
                }
            }
        }

        // Fetch full chapters if not already populated for thumbnail preview scrub
        if (item.Chapters == null || item.Chapters.Count == 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var fullItem = await client.GetItemAsync(item.Id);
                    if (fullItem?.Chapters != null && fullItem.Chapters.Count > 0)
                    {
                        item.Chapters = fullItem.Chapters;
                        Application.Current?.Dispatcher?.Invoke(() => ChaptersUpdated?.Invoke());
                    }
                }
                catch { }
            });
        }
        else
        {
            Application.Current?.Dispatcher?.Invoke(() => ChaptersUpdated?.Invoke());
        }

        await client.ReportPlaybackStartAsync(item.Id, mediaSourceId, startTicks);
        await _trakt.ScrobbleStartAsync(item, item.ProgressPercentage);
        _db.RecordWatchEvent(item.Id, srv?.Url ?? client.BaseUrl, item.Name, item.Type, (int)((item.RunTimeTicks ?? 0) / 10_000_000), item.SeriesName, item.Genres);

        if (item.Type == "Episode")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var imdbId = item.SeriesImdbId ?? string.Empty;
                    if (string.IsNullOrEmpty(imdbId) && !string.IsNullOrEmpty(item.SeriesId))
                    {
                        var cachedSeries = _db.GetCachedItem(item.SeriesId, item.ServerId ?? _settings.Current.ActiveServerId);
                        imdbId = cachedSeries?.ImdbId ?? string.Empty;
                        if (string.IsNullOrEmpty(imdbId))
                        {
                            var seriesItem = await client.GetItemAsync(item.SeriesId);
                            imdbId = seriesItem?.ImdbId ?? string.Empty;
                        }
                    }
                    if (string.IsNullOrEmpty(imdbId))
                    {
                        imdbId = item.ImdbId;
                    }
                    if (string.IsNullOrEmpty(imdbId))
                    {
                        imdbId = DetailVM.Item?.ImdbId ?? string.Empty;
                    }

                    if (!string.IsNullOrEmpty(imdbId))
                    {
                        await _introDb.LoadSegmentsForEpisodeAsync(imdbId, item.ParentIndexNumber ?? 1, item.IndexNumber ?? 1);
                    }
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("MainViewModel", $"Failed to resolve Series IMDb ID: {ex.Message}");
                }
            });
        }
        else
        {
            _introDb.Clear();
        }

        if (_watchParty.IsActive)
        {
            _watchParty.MarkShowLive();
        }
    }

    public async Task PlayNextEpisodeAsync()
    {
        if (_nextEpisodeItem != null)
        {
            var next = _nextEpisodeItem;
            CurrentPlaybackSeconds = 0;
            TotalDurationSeconds = 0;
            _lastPlaybackSeconds = 0;
            UpdateTimeString();
            await FinalizeCurrentPlaybackAsync(isEof: true);
            await StartPlaybackAsync(next, 0);
        }
    }

    public async Task PlayPrevEpisodeAsync()
    {
        if (_prevEpisodeItem != null)
        {
            var prev = _prevEpisodeItem;
            CurrentPlaybackSeconds = 0;
            TotalDurationSeconds = 0;
            _lastPlaybackSeconds = 0;
            UpdateTimeString();
            await FinalizeCurrentPlaybackAsync(isEof: false);
            await StartPlaybackAsync(prev, 0);
        }
    }

    public void ExecuteSkipPrompt()
    {
        if (IsSkipPromptVisible && _skipTargetSeconds > 0)
        {
            _ = _mpv.SeekTo(_skipTargetSeconds);
            if (_watchParty.IsActive)
            {
                _watchParty.BroadcastSeek(_skipTargetSeconds);
            }
            IsSkipPromptVisible = false;
        }
    }

    private async Task FinalizeCurrentPlaybackAsync(bool isEof)
    {
        if (_currentPlayingItem == null) return;
        var item = _currentPlayingItem;
        _currentPlayingItem = null;

        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
               ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
        var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
            ? _embyClient
            : EmbyClient.CreateForServer(srv);

        var mediaSourceId = item.MediaSources?.FirstOrDefault()?.Id ?? item.Id;
        var positionTicks = isEof
            ? (item.RunTimeTicks ?? (long)(_lastPlaybackSeconds * 10_000_000.0))
            : (long)(_lastPlaybackSeconds * 10_000_000.0);
        var progress = isEof ? 100.0 : (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0
            ? Math.Clamp((double)positionTicks / item.RunTimeTicks.Value * 100.0, 0, 100)
            : 0);

        try
        {
            await _trakt.ScrobbleStopAsync(item, progress);
        }
        catch { }

        try
        {
            await client.ReportPlaybackStoppedAsync(item.Id, mediaSourceId, positionTicks);
        }
        catch { }

        if (isEof || progress >= 90.0)
        {
            try
            {
                await client.MarkPlayedAsync(item.Id);
            }
            catch { }

            if (item.UserData != null)
            {
                item.UserData.Played = true;
                item.UserData.PlaybackPositionTicks = 0;
            }
            _db.RemoveFromContinueWatchingCache(item.Id);
            _db.ClearResumeProgress(item.Id);
        }
        else if (_lastPlaybackSeconds > 5)
        {
            item.UserData ??= new UserItemData();
            item.UserData.PlaybackPositionTicks = positionTicks;
            item.UserData.Played = false;
            _db.UpdatePlaybackProgress(item.Id, item.ServerId ?? string.Empty, positionTicks);
        }

        try
        {
            _db.RecordWatchEvent(item.Id, srv?.Url ?? client.BaseUrl, item.Name, item.Type, (int)_lastPlaybackSeconds, item.SeriesName, item.Genres);
        }
        catch { }
    }

    public async Task StopPlaybackAsync(bool isEof = false)
    {
        if (!IsPlayerActive && !_mpv.IsPlaying) return;

        // Cut off MPV audio and video playback immediately so no audio bleeds into the UI while scrobbling/reporting
        var stopTask = _mpv.StopAsync();

        if (_watchParty.IsActive)
        {
            _watchParty.BroadcastPause(_lastPlaybackSeconds);
        }

        IsPlayerActive = false;
        CurrentPlaybackSeconds = 0;
        TotalDurationSeconds = 0;
        _lastPlaybackSeconds = 0;
        UpdateTimeString();
        _introDb.Clear();
        IsSkipPromptVisible = false;

        await FinalizeCurrentPlaybackAsync(isEof);

        await stopTask;
    }

    public string? GetChapterImageUrl(int chapterIndex, string? tag = null)
    {
        if (_currentPlayingItem == null) return null;
        var item = _currentPlayingItem;
        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
               ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
        var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
            ? _embyClient
            : EmbyClient.CreateForServer(srv);

        return client.BuildChapterImageUrl(item.Id, chapterIndex, tag, 320, srv?.Url, srv?.AccessToken);
    }

    public string? GetPreviewFallbackImageUrl()
    {
        if (_currentPlayingItem == null) return null;
        var item = _currentPlayingItem;
        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
               ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
        var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
            ? _embyClient
            : EmbyClient.CreateForServer(srv);

        var backdropTag = item.BackdropImageTags?.FirstOrDefault();
        if (!string.IsNullOrEmpty(backdropTag))
        {
            return client.BuildImageUrl(item.Id, "Backdrop", 500, backdropTag, srv?.Url, srv?.AccessToken);
        }
        var primaryTag = item.ImageTags?.GetValueOrDefault("Primary");
        return client.BuildImageUrl(item.Id, "Primary", 500, primaryTag, srv?.Url, srv?.AccessToken);
    }
}
