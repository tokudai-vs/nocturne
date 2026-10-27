using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Nocturne.App.Services;
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
    private readonly ThemeColorService _themeColors;
    private readonly ITraktScrobbleService _traktScrobble;
    private readonly ITraktSyncService _traktSync;
    private readonly ITmdbEnrichmentService _tmdbEnrichment;
    private readonly TraktQueueDrainWorker _traktDrain;
    private readonly ITraktAuthService _traktAuth;
    private readonly IWatchlistService _watchlist;
    private readonly EnrichmentGate _enrichment;
    private PeerSyncScheduler? _peerSync;
    private readonly PlaybackSession _session;

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
    private double _currentPlaybackSeconds = 0;
    private double _totalDurationSeconds = 0;
    private bool _isPaused;
    private double _volume = 100;
    private string _timeString = "00:00 / 00:00";

    public HomeViewModel HomeVM { get; }
    public WatchlistViewModel WatchlistVM { get; }
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
        ThemeColorService themeColors,
        HomeViewModel homeVM,
        WatchlistViewModel watchlistVM,
        LibraryViewModel libraryVM,
        DetailViewModel detailVM,
        SearchViewModel searchVM,
        SettingsViewModel settingsVM,
        AnalyticsViewModel analyticsVM,
        OnboardingViewModel onboardingVM,
        ITraktScrobbleService traktScrobble,
        ITraktSyncService traktSync,
        ITmdbEnrichmentService tmdbEnrichment,
        TraktQueueDrainWorker traktDrain,
        ITraktAuthService traktAuth,
        IWatchlistService watchlist,
        EnrichmentGate enrichment)
    {
        _settings = settings;
        _embyClient = embyClient;
        _mpv = mpv;
        _introDb = introDb;
        _trakt = trakt;
        _db = db;
        _watchParty = watchParty;
        _sync = sync;
        _themeColors = themeColors;
        _themeColors.ThemeColorChanged += _ => UpdateTopBarColors();
        _traktScrobble = traktScrobble;
        _traktSync = traktSync;
        _tmdbEnrichment = tmdbEnrichment;
        _traktDrain = traktDrain;
        _traktAuth = traktAuth;
        _watchlist = watchlist;
        _enrichment = enrichment;
        _session = new PlaybackSession(db);

        HomeVM = homeVM;
        WatchlistVM = watchlistVM;
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

        // TMDB fallback enrichment runs when a sync completes (D12)
        _sync.SyncCompleted += () => _enrichment.RunAsync();

        // Wire playback requests to embedded player
        HomeVM.PlayRequested += item => _ = StartPlaybackAsync(item, item.UserData?.PlaybackPositionTicks ?? 0);
        DetailVM.PlayRequested += (item, ticks) => _ = StartPlaybackAsync(item, ticks);

        // Sync and Cache refresh handlers
        _sync.ProgressReported += report =>
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                // Partial and Error used to fall into the "still syncing" branch or just hide the pill: a failed or
                // incomplete sync left "Syncing..." up for good or vanished without a word. Both now leave a warning.
                if (report.State == SyncState.Partial || report.State == SyncState.Error)
                {
                    IsSyncing = false;
                    SyncProgressPercent = 0;
                    SyncProblemText = report.StatusMessage;
                }
                else if (report.State == SyncState.Done || report.State == SyncState.Idle)
                {
                    IsSyncing = false;
                    SyncProgressPercent = 0;
                }
                else
                {
                    SyncProblemText = string.Empty;
                    IsSyncing = true;
                    SyncProgressPercent = Math.Clamp(report.Percentage, 0, 100);
                    HeaderSyncStatusText = $"{report.StatusMessage} ({report.Percentage:F0}%)";
                }
            });
        };

        // BeginInvoke: the handler can raise the modal library-setup prompt, and SyncCompleted fires on the sync
        // thread inside DedupEngine.WriterGate; waiting on the dialog held the gate (and the cascade-gate
        // measurement) until the user answered.
        _sync.SyncCompleted += () =>
        {
            Application.Current?.Dispatcher?.BeginInvoke(() =>
            {
                IsSyncing = false;
                SyncProblemText = string.Empty;
                if (!_settings.Current.FirstLaunchComplete) ConfirmLibrarySetup();
                _ = HomeVM.LoadAsync();
                _ = LibraryVM.LoadViewsAsync();
            });
        };

        // Raised after the sync's gate measurement, which follows SyncCompleted (whose handler clears the pill).
        _sync.DedupAlertRaised += alert => Application.Current?.Dispatcher?.BeginInvoke(() => SyncProblemText = alert);

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
            _session.OnPosition(sec);
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
            _session.OnDuration(dur);
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                TotalDurationSeconds = dur;
                UpdateTimeString();
            });
        };

        _mpv.PauseChanged += paused =>
        {
            _ = _session.ReportProgressAsync(paused);
            if (_watchParty.IsActive)
            {
                if (paused) _watchParty.BroadcastPause(_mpv.CurrentPositionSeconds);
                else _watchParty.BroadcastPlay(_mpv.CurrentPositionSeconds);
            }
            if (_currentPlayingItem != null && long.TryParse(_currentPlayingItem.DedupGroupId, out var gid) && gid > 0)
            {
                var dur = _totalDurationSeconds > 0 ? _totalDurationSeconds : (_currentPlayingItem.RunTimeTicks.HasValue ? _currentPlayingItem.RunTimeTicks.Value / 10_000_000.0 : 0);
                var prog = dur > 0 ? Math.Clamp(_currentPlaybackSeconds / dur * 100.0, 0, 100) : 0;
                if (paused)
                {
                    _traktScrobble.Pause(gid, prog);
                }
                else
                {
                    _traktScrobble.Start(gid, prog);
                }
                _ = _traktDrain.DrainAsync(System.Threading.CancellationToken.None);
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

        _mpv.PlaybackEnded += endReason =>
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
                        // Only a real end of file is a completion candidate; stop, quit and error are stops.
                        var reason = endReason == MpvEndReason.Eof ? PlaybackStopReason.EndOfFile : PlaybackStopReason.Stop;
                        if (reason == PlaybackStopReason.EndOfFile && _session.WouldComplete(reason) &&
                            _settings.Current.AutoPlayNextEpisode && CanGoNextEpisode && _nextEpisodeItem != null)
                        {
                            NocturneLogger.Info("MainViewModel", $"Auto-advancing to next episode: {_nextEpisodeItem.Name}");
                            await PlayNextEpisodeAsync(reason);
                        }
                        else
                        {
                            await StopPlaybackAsync(reason);
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

    private bool _isScrolledOverContent;
    public bool IsScrolledOverContent
    {
        get => _isScrolledOverContent;
        set
        {
            if (SetProperty(ref _isScrolledOverContent, value))
            {
                UpdateTopBarColors();
            }
        }
    }

    public BaseViewModel CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (SetProperty(ref _currentViewModel, value))
            {
                _isScrolledOverContent = false;
                IsNavigationVisible = value is not OnboardingViewModel;
                OnPropertyChanged(nameof(IsHomeActive));
                OnPropertyChanged(nameof(IsWatchlistActive));
                OnPropertyChanged(nameof(IsLibraryActive));
                OnPropertyChanged(nameof(IsDetailActive));
                OnPropertyChanged(nameof(IsSearchActive));
                OnPropertyChanged(nameof(IsSettingsActive));
                OnPropertyChanged(nameof(IsAnalyticsActive));
                OnPropertyChanged(nameof(IsOnboardingActive));
                OnPropertyChanged(nameof(IsHeroPage));
                UpdateTopBarColors();
            }
        }
    }

    public bool IsHomeActive => CurrentViewModel is HomeViewModel;
    public bool IsWatchlistActive => CurrentViewModel is WatchlistViewModel;
    public bool IsLibraryActive => CurrentViewModel is LibraryViewModel;
    public bool IsDetailActive => CurrentViewModel is DetailViewModel;
    public bool IsSearchActive => CurrentViewModel is SearchViewModel;
    public bool IsSettingsActive => CurrentViewModel is SettingsViewModel;
    public bool IsAnalyticsActive => CurrentViewModel is AnalyticsViewModel;
    public bool IsOnboardingActive => CurrentViewModel is OnboardingViewModel;
    public bool IsHeroPage => CurrentViewModel is HomeViewModel || CurrentViewModel is DetailViewModel;

    public bool IsThemeLight
    {
        get
        {
            try
            {
                return Wpf.Ui.Appearance.ApplicationThemeManager.GetAppTheme() == Wpf.Ui.Appearance.ApplicationTheme.Light;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsOverHeroBanner => IsHeroPage && !IsScrolledOverContent;

    public Brush TopBarForeground
    {
        get
        {
            if (IsOverHeroBanner || !IsThemeLight)
            {
                return Brushes.White;
            }
            return (Brush)(Application.Current?.TryFindResource("TextFillColorPrimaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)));
        }
    }

    public Brush TopBarSecondaryForeground
    {
        get
        {
            if (IsOverHeroBanner || !IsThemeLight)
            {
                return new SolidColorBrush(Color.FromArgb(0xDC, 0xFF, 0xFF, 0xFF));
            }
            return (Brush)(Application.Current?.TryFindResource("TextFillColorSecondaryBrush") ?? new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x70)));
        }
    }

    public Brush TopBarBackgroundBrush
    {
        get
        {
            if (IsOverHeroBanner)
            {
                var gradient = new LinearGradientBrush
                {
                    StartPoint = new Point(0.5, 0),
                    EndPoint = new Point(0.5, 1)
                };
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xE8, 0x00, 0x00, 0x00), 0.0));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0xB8, 0x00, 0x00, 0x00), 0.5));
                gradient.GradientStops.Add(new GradientStop(Color.FromArgb(0x60, 0x00, 0x00, 0x00), 1.0));
                gradient.Freeze();
                return gradient;
            }

            if (!IsThemeLight)
            {
                var darkBrush = new SolidColorBrush(Color.FromArgb(0xF2, 0x14, 0x14, 0x18));
                darkBrush.Freeze();
                return darkBrush;
            }
            else
            {
                var lightBrush = new SolidColorBrush(Color.FromArgb(0xF5, 0xF8, 0xF8, 0xFC));
                lightBrush.Freeze();
                return lightBrush;
            }
        }
    }

    public Brush TopBarBorderBrush
    {
        get
        {
            if (IsOverHeroBanner)
            {
                return Brushes.Transparent;
            }
            return !IsThemeLight
                ? new SolidColorBrush(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF))
                : new SolidColorBrush(Color.FromArgb(0x18, 0x00, 0x00, 0x00));
        }
    }

    public Brush TopBarSyncPillBackgroundBrush
    {
        get
        {
            if (IsOverHeroBanner || !IsThemeLight)
            {
                var b = new SolidColorBrush(Color.FromArgb(0x35, 0x00, 0x00, 0x00));
                b.Freeze();
                return b;
            }
            else
            {
                var b = new SolidColorBrush(Color.FromArgb(0x14, 0x00, 0x00, 0x00));
                b.Freeze();
                return b;
            }
        }
    }

    public Brush TopBarSyncPillBorderBrush => (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange);

    public Brush TopBarSyncIconBrush => (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange);

    public Brush TopBarSyncTextBrush => TopBarForeground;

    public Brush HomeTabIconBrush => IsHomeActive
        ? (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange)
        : TopBarSecondaryForeground;

    public Brush WatchlistTabIconBrush => IsWatchlistActive
        ? (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange)
        : TopBarSecondaryForeground;

    public Brush LibraryTabIconBrush => IsLibraryActive
        ? (Brush)(Application.Current?.TryFindResource("NocturneAmberBrush") ?? Brushes.Orange)
        : TopBarSecondaryForeground;

    public void UpdateTopBarColors()
    {
        OnPropertyChanged(nameof(IsThemeLight));
        OnPropertyChanged(nameof(IsOverHeroBanner));
        OnPropertyChanged(nameof(TopBarForeground));
        OnPropertyChanged(nameof(TopBarSecondaryForeground));
        OnPropertyChanged(nameof(TopBarBackgroundBrush));
        OnPropertyChanged(nameof(TopBarBorderBrush));
        OnPropertyChanged(nameof(TopBarSyncPillBackgroundBrush));
        OnPropertyChanged(nameof(TopBarSyncPillBorderBrush));
        OnPropertyChanged(nameof(TopBarSyncIconBrush));
        OnPropertyChanged(nameof(TopBarSyncTextBrush));
        OnPropertyChanged(nameof(HomeTabIconBrush));
        OnPropertyChanged(nameof(WatchlistTabIconBrush));
        OnPropertyChanged(nameof(LibraryTabIconBrush));
    }

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

    private bool _isStartupFailed;

    // Startup error state: the splash stops spinning and says what failed instead of waiting forever.
    public bool IsStartupFailed
    {
        get => _isStartupFailed;
        private set => SetProperty(ref _isStartupFailed, value);
    }

    public void ShowStartupError(Exception ex)
    {
        IsStartupFailed = true;
        SplashStatusText = $"Nocturne could not finish starting: {ex.Message}\nDetails are in nocturne.log.";
    }

    public bool IsNavigationVisible
    {
        get => _isNavigationVisible;
        set => SetProperty(ref _isNavigationVisible, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        set
        {
            if (SetProperty(ref _isSyncing, value)) _enrichment.SetSyncing(value); // D12
        }
    }

    public string HeaderSyncStatusText
    {
        get => _headerSyncStatusText;
        set => SetProperty(ref _headerSyncStatusText, value);
    }

    private string _syncProblemText = string.Empty;
    public string SyncProblemText
    {
        get => _syncProblemText;
        set
        {
            if (SetProperty(ref _syncProblemText, value)) OnPropertyChanged(nameof(HasSyncProblem));
        }
    }
    public bool HasSyncProblem => !string.IsNullOrEmpty(_syncProblemText);

    // Library setup after the first completed sync (v3.5's LibraryMappingWizard step; v4 went straight to
    // separate mode and never asked). With one server there is nothing to choose. The groups shown are the ones
    // the sync maintains from each library's type; Settings edits them.
    private void ConfirmLibrarySetup()
    {
        var s = _settings.Current;
        if (s.Servers.Count < 2)
        {
            _settings.Update(x => x.FirstLaunchComplete = true);
            return;
        }
        var groups = string.Join("\n", s.CombinedMappings.Values.Select(m =>
            $"• {m.Name}: {string.Join(", ", m.Libraries.Select(l => $"{l.LibraryName} ({l.ServerName})"))}"));
        var choice = System.Windows.MessageBox.Show(
            $"You have {s.Servers.Count} servers. Combine them into one library, so a title on both is shown once with a version picker?\n\n{groups}\n\n" +
            "Yes: one combined library.\nNo: keep each server separate.\n\nBoth the mode and the grouping can be changed in Settings.",
            "Library setup", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        _settings.Update(x =>
        {
            x.LibraryMode = choice == System.Windows.MessageBoxResult.Yes ? "combined" : "separate";
            x.FirstLaunchComplete = true;
        });
        OnPropertyChanged(nameof(IsSwitchServerVisible));
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
        set
        {
            if (SetProperty(ref _isPlayerActive, value)) _enrichment.SetPlaying(value); // D12
        }
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

    public void NavigateToWatchlist()
    {
        CurrentViewModel = WatchlistVM;
        _ = WatchlistVM.RefreshAsync();
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

    public void OpenGroupDetail(long groupId)
    {
        var group = _db.GetDedupGroup(groupId);
        if (group != null && !string.IsNullOrEmpty(group.PrimaryItemId))
        {
            var cached = _db.GetCachedItem(group.PrimaryItemId, group.PrimaryServerId ?? string.Empty);
            if (cached != null)
            {
                NavigateToDetail(cached);
                return;
            }
        }

        var fallback = new EmbyItem
        {
            Id = group?.PrimaryItemId ?? groupId.ToString(),
            DedupGroupId = groupId.ToString(),
            ServerId = group?.PrimaryServerId,
            Name = group?.Name ?? "Item",
            Type = group?.ItemType ?? "Movie",
            ProductionYear = group?.Year,
            Overview = group?.Overview
        };
        NavigateToDetail(fallback);
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
            _ = StartPeerSyncAsync();

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

    // I5: refresh the Trakt token at startup (it was only refreshed before a queue drain, so an expired token
    // stayed "connected" until the first scrobble), then start the peer cycle: now, after Connect, every 15 minutes.
    private async Task StartPeerSyncAsync()
    {
        try
        {
            // One-time move of the legacy settings Client ID into the auth stack that is actually used.
            var legacyId = _settings.Current.TraktClientIdOverride;
            if (string.IsNullOrWhiteSpace(_traktAuth.CustomClientId) && !string.IsNullOrWhiteSpace(legacyId))
            {
                _traktAuth.SetCustomClientId(legacyId);
                _settings.Update(s => s.TraktClientIdOverride = null);
                NocturneLogger.Info("MainViewModel", "Moved the Trakt Client ID from the legacy settings field to the Trakt auth service.");
            }
            await _traktAuth.RefreshIfNeededAsync(System.Threading.CancellationToken.None);
            _peerSync = new PeerSyncScheduler(_traktSync, _traktAuth, () => IsPlayerActive, DrainOutboundAsync,
                () => _session.DrainSiblingWritesAsync(SiblingClient, freshOnly: true));
            _peerSync.Start();
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("MainViewModel", $"Trakt startup: {ex.Message}");
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

        CurrentPlaybackSeconds = startTicks / 10_000_000.0;
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
                        // Live results carry no owner; stamp the server they came from so the next / previous
                        // episode plays and finalizes against it.
                        foreach (var ep in eps) ep.ServerId ??= srv?.Id;
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

    public List<EmbyItem> GetOrderedPlaybackCandidates(EmbyItem item)
    {
        var candidates = new List<EmbyItem>();

        // 1. If item has explicit FallbackCopies pre-populated from user selection, respect that tier first
        if (item.FallbackCopies != null && item.FallbackCopies.Count > 0)
        {
            candidates.Add(item);
            candidates.AddRange(item.FallbackCopies);
        }

        // 2. Fetch all sibling copies across all servers from SQLite
        var siblings = _db.GetSiblingVersions(item);
        if (siblings.Count == 0) siblings.Add(item);

        var priorityList = _settings.Current.PreferredServerPriority ?? new List<string>();

        // Order siblings by Playback Decision Matrix:
        // Quality Rank (4K > 1080p > 720p > SD, edition, HDR) > Bitrate > Server Priority
        var ordered = siblings
            .OrderByDescending(s => DetailViewModel.GetQualityRank(s))
            .ThenByDescending(s => s.MeasuredBitrate ?? 0)
            .ThenBy(s =>
            {
                var idx = priorityList.IndexOf(s.ServerId ?? string.Empty);
                return idx >= 0 ? idx : int.MaxValue;
            });

        foreach (var s in ordered)
        {
            if (!candidates.Any(c => c.Id == s.Id && c.ServerId == s.ServerId))
            {
                candidates.Add(s);
            }
        }

        return candidates;
    }

    public async Task ExecuteEmbeddedPlaybackAsync(IntPtr hwnd, EmbyItem item, long startTicks)
    {
        _currentSubtitleTrackId = null;
        _currentAudioTrackId = null;
        OnPropertyChanged(nameof(CurrentSubtitleTrackId));
        OnPropertyChanged(nameof(CurrentAudioTrackId));
        OnPropertyChanged(nameof(AvailableSubtitleStreams));
        OnPropertyChanged(nameof(AvailableAudioStreams));

        var candidates = GetOrderedPlaybackCandidates(item);
        bool started = false;
        Exception? lastError = null;
        EmbyClient? activeClient = null;
        EmbyItem? activeItem = null;
        ServerConfig? activeSrv = null;
        string? activeMediaSourceId = null;

        foreach (var candidate in candidates)
        {
            var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == candidate.ServerId)
                   ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
            var client = (srv == null || (srv.Id == _settings.Current.ActiveServerId && _embyClient.IsAuthenticated))
                ? _embyClient
                : EmbyClient.CreateForServer(srv);

            var mediaSourceId = candidate.MediaSources?.FirstOrDefault()?.Id ?? candidate.Id;
            var streamUrl = client.GetStreamUrl(candidate.Id, mediaSourceId, srv?.Url, srv?.AccessToken);

            try
            {
                NocturneLogger.Info("MainViewModel", $"Starting embedded mpv: {candidate.Name} ({candidate.Id}) on server '{srv?.Name}' (Quality: {candidate.TechnicalBadge}) at tick {startTicks} in HWND {hwnd}");
                await _mpv.PlayEmbeddedAsync(
                    hwnd,
                    streamUrl,
                    candidate.Name,
                    startTicks,
                    subFont: _settings.Current.SubtitleFont,
                    subSize: _settings.Current.SubtitleSize,
                    subColor: _settings.Current.SubtitleColor,
                    subBorder: _settings.Current.SubtitleBorder,
                    subBg: _settings.Current.SubtitleBackground);

                _currentPlayingItem = candidate;
                activeItem = candidate;
                activeClient = client;
                activeSrv = srv;
                activeMediaSourceId = mediaSourceId;
                started = true;
                break;
            }
            catch (Exception ex)
            {
                lastError = ex;
                NocturneLogger.Warn("MainViewModel", $"Playback failed on server '{srv?.Name}' for item '{candidate.Name}': {ex.Message}. Attempting automatic failover to sibling server...");
            }
        }

        if (!started || activeClient == null || activeItem == null)
        {
            NocturneLogger.Error("MainViewModel", "All playback candidates and sibling servers failed to start.", lastError);
            throw lastError ?? new InvalidOperationException("Failed to start playback on any available server.");
        }

        _session.Begin(activeItem, activeClient, activeMediaSourceId, startTicks);

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
                    var fullItem = await activeClient.GetItemAsync(item.Id);
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

        // Report the version that actually started (after failover it differs from the one requested).
        await activeClient.ReportPlaybackStartAsync(activeItem.Id, activeMediaSourceId ?? activeItem.Id, startTicks);
        if (long.TryParse(item.DedupGroupId, out var startGid) && startGid > 0)
        {
            var dur = (item.RunTimeTicks ?? 0) / 10_000_000.0;
            var prog = dur > 0 ? Math.Clamp((startTicks / 10_000_000.0) / dur * 100.0, 0, 100) : 0;
            _traktScrobble.Start(startGid, prog);
            _ = _traktDrain.DrainAsync(System.Threading.CancellationToken.None);
        }
        _db.RecordWatchEvent(item.Id, activeSrv?.Url ?? activeClient.BaseUrl, item.Name, item.Type, (int)((item.RunTimeTicks ?? 0) / 10_000_000), item.SeriesName, item.Genres);

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
                            var seriesItem = await activeClient.GetItemAsync(item.SeriesId);
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

    // reason is Next for a user skip; the auto-advance path passes EndOfFile.
    public async Task PlayNextEpisodeAsync(PlaybackStopReason reason = PlaybackStopReason.Next)
    {
        if (_nextEpisodeItem != null)
        {
            var next = _nextEpisodeItem;
            await FinalizeCurrentPlaybackAsync(reason);
            CurrentPlaybackSeconds = 0;
            TotalDurationSeconds = 0;
            UpdateTimeString();
            await StartPlaybackAsync(next, 0);
        }
    }

    public async Task PlayPrevEpisodeAsync()
    {
        if (_prevEpisodeItem != null)
        {
            var prev = _prevEpisodeItem;
            await FinalizeCurrentPlaybackAsync(PlaybackStopReason.Previous);
            CurrentPlaybackSeconds = 0;
            TotalDurationSeconds = 0;
            UpdateTimeString();
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

    private async Task FinalizeCurrentPlaybackAsync(PlaybackStopReason reason)
    {
        _currentPlayingItem = null;
        var result = await _session.FinalizeAsync(reason);
        if (result == null) return;
        var item = result.Item;

        if (long.TryParse(item.DedupGroupId, out var stopGid) && stopGid > 0)
        {
            _traktScrobble.Stop(stopGid, result.ProgressPercent);
            _ = _traktDrain.DrainAsync(System.Threading.CancellationToken.None);
        }

        _ = DrainSiblingWriteQueueAsync();

        try
        {
            var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == item.ServerId)
                   ?? _settings.Current.Servers.FirstOrDefault(s => s.Id == _settings.Current.ActiveServerId);
            _db.RecordWatchEvent(item.Id, srv?.Url ?? _embyClient.BaseUrl, item.Name, item.Type, (int)(result.PositionTicks / 10_000_000), item.SeriesName, item.Genres);
        }
        catch { }
    }

    // Nocturne's own writes to its peers: favourites to each server, then the Trakt queue.
    private async Task DrainOutboundAsync()
    {
        try
        {
            await _watchlist.DrainFavouritesAsync(SiblingClient);
            await _traktDrain.DrainAsync(System.Threading.CancellationToken.None);
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("MainViewModel", "Error draining watchlist writes", ex);
        }
    }

    // §9.3/§12.5: the one watchlist toggle behind every poster, context menu and detail page. Optimistic: the entries
    // and their pending ops are written at once, then both peers are drained; a peer that is offline gets it on the
    // next cycle. A title outside the library (no group) can be on the Trakt watchlist only.
    public async Task SetWatchlistedAsync(EmbyItem item, bool listed)
    {
        item.IsInWatchlist = listed;
        var none = System.Threading.CancellationToken.None;
        if (long.TryParse(item.DedupGroupId, out var gid) && gid > 0)
            await (listed ? _watchlist.AddAsync(gid, none) : _watchlist.RemoveAsync(gid, none));
        else if (TitleRef(item) is { } titleRef)
            await (listed ? _watchlist.AddUnownedAsync(titleRef, item.Name, item.ProductionYear, item.Type ?? "Movie", none) : _watchlist.RemoveUnownedAsync(titleRef, none));
        _ = DrainOutboundAsync();
    }

    public async Task RemoveWatchlistCardAsync(WatchlistCard card)
    {
        var none = System.Threading.CancellationToken.None;
        await (card.GroupId > 0 ? _watchlist.RemoveAsync(card.GroupId, none) : _watchlist.RemoveUnownedAsync(card.Ref, none));
        _ = DrainOutboundAsync();
    }

    // The Trakt entry's ref for a title outside the library, as a pull writes it.
    public static string? TitleRef(EmbyItem item) => ProviderKey.Title(item.Type, Provider.Tmdb, item.TmdbId)?.ToString();

    public static bool IsWatchlistable(EmbyItem item) =>
        string.Equals(item.Type, "Movie", StringComparison.OrdinalIgnoreCase) || string.Equals(item.Type, "Series", StringComparison.OrdinalIgnoreCase);

    private EmbyClient? SiblingClient(string serverId)
    {
        var srv = _settings.Current.Servers.FirstOrDefault(s => s.Id == serverId);
        return srv == null ? null : EmbyClient.CreateForServer(srv);
    }

    private async Task DrainSiblingWriteQueueAsync()
    {
        try
        {
            await _session.DrainSiblingWritesAsync(SiblingClient);
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("MainViewModel", "Error draining sibling write queue", ex);
        }
    }

    public async Task StopPlaybackAsync(PlaybackStopReason reason = PlaybackStopReason.Stop)
    {
        if (!IsPlayerActive && !_mpv.IsPlaying) return;

        // Cut off MPV audio and video playback immediately so no audio bleeds into the UI while scrobbling/reporting
        var stopTask = _mpv.StopAsync();

        if (_watchParty.IsActive)
        {
            _watchParty.BroadcastPause(_session.PositionSeconds);
        }

        IsPlayerActive = false;
        _introDb.Clear();
        IsSkipPromptVisible = false;

        // Finalize reads the position from the session, so the UI reset below cannot zero it first.
        await FinalizeCurrentPlaybackAsync(reason);

        CurrentPlaybackSeconds = 0;
        TotalDurationSeconds = 0;
        UpdateTimeString();

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
        var servers = _settings.Current.Servers;
        _db.AttachGroupArtwork(new[] { item }); // spec §3.3: the group's elected artwork, from its version's own server
        return Artwork.Backdrop(item, servers, 500) ?? Artwork.Poster(item, servers, 500);
    }
}
