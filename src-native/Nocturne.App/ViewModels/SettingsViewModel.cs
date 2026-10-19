using System.Collections.ObjectModel;
using Wpf.Ui.Appearance;
using Nocturne.App.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Services;

namespace Nocturne.App.ViewModels;

public class SettingsViewModel : BaseViewModel
{
    private readonly SettingsService _settings;
    private readonly EmbyClient _embyClient;
    private readonly DatabaseService _db;
    private readonly DedupEngine _dedup;
    private readonly SyncEngine _sync;
    private readonly TraktService _trakt;
    private readonly ThemeColorService _themeColors;
    private readonly EmbyConnectService _connect;

    // Server adding fields
    private string _newServerUrl = "http://";
    private string _newUsername = string.Empty;
    private string _newPassword = string.Empty;
    private string _serverAddError = string.Empty;
    private bool _isAddingServer;

    // Appearance
    private string _selectedTheme = "auto";
    private string _selectedBackdrop = "Mica";
    private string _selectedAccentColor = "#E5A00D";

    // Playback & Skip
    private string _preferredQuality = "highest";
    private bool _autoPlayNextEpisode = true;
    private string _skipIntroMode = "button";
    private string _skipRecapMode = "auto";
    private string _skipCreditsMode = "button";

    // Subtitles
    private string _subtitleFont = "Segoe UI Variable Text";
    private int _subtitleSize = 46;
    private string _subtitleColor = "#FFFFFF";
    private string _subtitleBackground = "none";
    private string _defaultSubtitleLanguage = "none";
    private string _defaultAudioLanguage = "eng";

    // Library Mode & Dedup
    private string _libraryMode = "separate";
    private bool _showUnmappedLibraries = true;
    private bool _dedupEnabled = true;
    private int _dedupGroupsCount;
    private int _cachedItemsCount;
    private string _databaseSizeText = "0 MB";

    // Combined Group add
    private string _newGroupName = string.Empty;
    private string _newGroupIcon = "Film";

    // Sync & Trakt
    private string _syncStatusText = "Idle";
    private bool _isSyncing;
    private string? _traktAuthCode;
    private string? _traktAuthUrl;
    private bool _traktAutoScrobble = true;
    private bool _traktSyncWatchedState = true;
    private bool _traktShowWatchlistInSidebar = true;
    private string _traktClientId = string.Empty;
    private string _traktClientSecret = string.Empty;

    public ObservableCollection<ServerConfig> Servers { get; } = new();
    public ObservableCollection<CombinedMapping> CombinedGroups { get; } = new();

    public SettingsViewModel(
        SettingsService settings,
        EmbyClient embyClient,
        DatabaseService db,
        DedupEngine dedup,
        SyncEngine sync,
        TraktService trakt,
        ThemeColorService themeColors,
        EmbyConnectService connect)
    {
        _settings = settings;
        _embyClient = embyClient;
        _db = db;
        _dedup = dedup;
        _sync = sync;
        _trakt = trakt;
        _themeColors = themeColors;
        _connect = connect;

        LoadFromSettings();

        _sync.StateChanged += (state, msg) =>
        {
            SyncStatusText = msg;
            IsSyncing = state != SyncState.Idle && state != SyncState.Done && state != SyncState.Error;
            if (state == SyncState.Done)
            {
                RefreshDatabaseStats();
            }
        };

        RefreshDatabaseStats();
    }

    private void LoadFromSettings()
    {
        var s = _settings.Current;
        _selectedTheme = s.Theme;
        _selectedBackdrop = s.BackdropMaterial;
        _selectedAccentColor = s.AccentColor;
        _preferredQuality = s.PreferredQuality;
        _autoPlayNextEpisode = s.AutoPlayNextEpisode;
        _skipIntroMode = s.SkipIntroMode;
        _skipRecapMode = s.SkipRecapMode;
        _skipCreditsMode = s.SkipCreditsMode;

        _subtitleFont = s.SubtitleFont;
        _subtitleSize = s.SubtitleSize;
        _subtitleColor = s.SubtitleColor;
        _subtitleBackground = s.SubtitleBackground;
        _defaultSubtitleLanguage = s.DefaultSubtitleLanguage;
        _defaultAudioLanguage = s.DefaultAudioLanguage;

        _libraryMode = s.LibraryMode;
        _showUnmappedLibraries = s.ShowUnmappedLibraries;
        _dedupEnabled = s.DedupEnabled;

        _traktAutoScrobble = s.TraktAutoScrobble;
        _traktSyncWatchedState = s.TraktSyncWatchedState;
        _traktShowWatchlistInSidebar = s.TraktShowWatchlistInSidebar;
        _traktClientId = s.TraktClientIdOverride;
        _traktClientSecret = s.TraktClientSecretOverride;

        LoadServers();
        LoadCombinedGroups();
    }

    public void LoadServers()
    {
        Servers.Clear();
        foreach (var s in _settings.Current.Servers)
        {
            Servers.Add(s);
        }
    }

    public void LoadCombinedGroups()
    {
        CombinedGroups.Clear();
        foreach (var kv in _settings.Current.CombinedMappings)
        {
            CombinedGroups.Add(kv.Value);
        }
    }

    public void RefreshDatabaseStats()
    {
        try
        {
            CachedItemsCount = _db.GetCachedItemsCount();
            DedupGroupsCount = _db.GetDedupGroupsCount();
            var sizeBytes = _db.GetDatabaseSize();
            DatabaseSizeText = $"{sizeBytes / (1024.0 * 1024.0):F1} MB";
        }
        catch { }
    }

    // Server Properties
    public string ActiveServerId => _settings.Current.ActiveServerId ?? string.Empty;

    public string NewServerUrl
    {
        get => _newServerUrl;
        set => SetProperty(ref _newServerUrl, value);
    }

    public string NewUsername
    {
        get => _newUsername;
        set => SetProperty(ref _newUsername, value);
    }

    public string NewPassword
    {
        get => _newPassword;
        set => SetProperty(ref _newPassword, value);
    }

    public string ServerAddError
    {
        get => _serverAddError;
        set => SetProperty(ref _serverAddError, value);
    }

    public bool IsAddingServer
    {
        get => _isAddingServer;
        set => SetProperty(ref _isAddingServer, value);
    }

    public async Task AddServerAsync()
    {
        ServerAddError = string.Empty;
        if (string.IsNullOrWhiteSpace(NewServerUrl) || string.IsNullOrWhiteSpace(NewUsername))
        {
            ServerAddError = "Server URL and Username are required.";
            return;
        }

        IsAddingServer = true;
        try
        {
            var cleanUrl = EmbyClient.NormalizeServerUrl(NewServerUrl);
            var auth = await _embyClient.AuthenticateAsync(cleanUrl, NewUsername, NewPassword);
            if (auth != null && auth.User != null)
            {
                var newServer = new ServerConfig
                {
                    Id = Guid.NewGuid().ToString("N")[..8],
                    Name = auth.User.Name + "'s Server",
                    Url = cleanUrl,
                    UserId = auth.User.Id,
                    Username = auth.User.Name,
                    AccessToken = auth.AccessToken,
                    Version = auth.ServerId,
                    AddedAt = DateTime.UtcNow,
                    LastConnected = DateTime.UtcNow
                };

                _settings.Update(s =>
                {
                    s.Servers.RemoveAll(x => x.Url == cleanUrl && x.UserId == auth.User.Id);
                    s.Servers.Add(newServer);
                    s.ActiveServerId = newServer.Id;
                });

                LoadServers();
                OnPropertyChanged(nameof(ActiveServerId));
                NewUsername = string.Empty;
                NewPassword = string.Empty;

                _ = _sync.SyncAllAsync();
            }
            else
            {
                ServerAddError = "Failed to authenticate with server. Please check URL and credentials.";
            }
        }
        catch (Exception ex)
        {
            ServerAddError = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsAddingServer = false;
        }
    }

    public void SwitchActiveServer(string serverId)
    {
        var target = _settings.Current.Servers.FirstOrDefault(s => s.Id == serverId);
        if (target != null)
        {
            _settings.Update(s =>
            {
                s.ActiveServerId = serverId;
                target.LastConnected = DateTime.UtcNow;
            });
            _embyClient.Configure(target.Url, target.AccessToken, target.UserId);
            OnPropertyChanged(nameof(ActiveServerId));
            _ = _sync.SyncAllAsync();
        }
    }

    public void RemoveServer(string serverId)
    {
        _settings.Update(s =>
        {
            s.Servers.RemoveAll(x => x.Id == serverId);
            if (s.ActiveServerId == serverId)
            {
                s.ActiveServerId = s.Servers.FirstOrDefault()?.Id;
            }
        });

        _db.ClearServerData(serverId);
        LoadServers();
        OnPropertyChanged(nameof(ActiveServerId));
    }

    // Appearance Properties
    public string SelectedTheme
    {
        get => _selectedTheme;
        set
        {
            if (SetProperty(ref _selectedTheme, value))
            {
                _settings.Update(s => s.Theme = value);
                ApplyTheme(value);
            }
        }
    }

    public string SelectedBackdrop
    {
        get => _selectedBackdrop;
        set
        {
            if (SetProperty(ref _selectedBackdrop, value))
            {
                _settings.Update(s => s.BackdropMaterial = value);
            }
        }
    }

    public ObservableCollection<ThemeColorUiOption> ThemeColors { get; } = new(ThemeColorService.AvailableColors);

    public string SelectedAccentColor
    {
        get => _selectedAccentColor;
        set
        {
            if (SetProperty(ref _selectedAccentColor, value))
            {
                _themeColors.ApplyColor(value);
            }
        }
    }

    // Library Mode & Dedup
    public string LibraryMode
    {
        get => _libraryMode;
        set
        {
            if (SetProperty(ref _libraryMode, value))
            {
                _settings.Update(s => s.LibraryMode = value);
            }
        }
    }

    public bool ShowUnmappedLibraries
    {
        get => _showUnmappedLibraries;
        set
        {
            if (SetProperty(ref _showUnmappedLibraries, value))
            {
                _settings.Update(s => s.ShowUnmappedLibraries = value);
            }
        }
    }

    public bool DedupEnabled
    {
        get => _dedupEnabled;
        set
        {
            if (SetProperty(ref _dedupEnabled, value))
            {
                _settings.Update(s => s.DedupEnabled = value);
            }
        }
    }

    public int DedupGroupsCount
    {
        get => _dedupGroupsCount;
        set => SetProperty(ref _dedupGroupsCount, value);
    }

    public int CachedItemsCount
    {
        get => _cachedItemsCount;
        set => SetProperty(ref _cachedItemsCount, value);
    }

    public string DatabaseSizeText
    {
        get => _databaseSizeText;
        set => SetProperty(ref _databaseSizeText, value);
    }

    public string NewGroupName
    {
        get => _newGroupName;
        set => SetProperty(ref _newGroupName, value);
    }

    public string NewGroupIcon
    {
        get => _newGroupIcon;
        set => SetProperty(ref _newGroupIcon, value);
    }

    public void AddCombinedGroup()
    {
        if (string.IsNullOrWhiteSpace(NewGroupName)) return;
        var key = NewGroupName.Trim().ToLowerInvariant().Replace(" ", "-");
        var mapping = new CombinedMapping
        {
            Name = NewGroupName.Trim(),
            Icon = NewGroupIcon,
            Libraries = new List<CombinedLibraryRef>()
        };

        _settings.Update(s => s.CombinedMappings[key] = mapping);
        LoadCombinedGroups();
        NewGroupName = string.Empty;
    }

    public void RemoveCombinedGroup(string groupName)
    {
        _settings.Update(s =>
        {
            var match = s.CombinedMappings.FirstOrDefault(x => x.Value.Name == groupName);
            if (match.Key != null) s.CombinedMappings.Remove(match.Key);
        });
        LoadCombinedGroups();
    }

    public async Task RebuildDedupIndexAsync()
    {
        IsSyncing = true;
        SyncStatusText = "Rebuilding multi-server deduplication index...";
        await Task.Run(() =>
        {
            _dedup.RebuildIndex(_db);
        });
        RefreshDatabaseStats();
        IsSyncing = false;
        SyncStatusText = "Deduplication index rebuild complete.";
    }

    // Playback & Skip Segments
    public string PreferredQuality
    {
        get => _preferredQuality;
        set
        {
            if (SetProperty(ref _preferredQuality, value))
            {
                _settings.Update(s => s.PreferredQuality = value);
            }
        }
    }

    public bool AutoPlayNextEpisode
    {
        get => _autoPlayNextEpisode;
        set
        {
            if (SetProperty(ref _autoPlayNextEpisode, value))
            {
                _settings.Update(s => s.AutoPlayNextEpisode = value);
            }
        }
    }

    public string SkipIntroMode
    {
        get => _skipIntroMode;
        set
        {
            if (SetProperty(ref _skipIntroMode, value))
            {
                _settings.Update(s => s.SkipIntroMode = value);
            }
        }
    }

    public string SkipRecapMode
    {
        get => _skipRecapMode;
        set
        {
            if (SetProperty(ref _skipRecapMode, value))
            {
                _settings.Update(s => s.SkipRecapMode = value);
            }
        }
    }

    public string SkipCreditsMode
    {
        get => _skipCreditsMode;
        set
        {
            if (SetProperty(ref _skipCreditsMode, value))
            {
                _settings.Update(s => s.SkipCreditsMode = value);
            }
        }
    }

    // Subtitle Options
    public string SubtitleFont
    {
        get => _subtitleFont;
        set
        {
            if (SetProperty(ref _subtitleFont, value))
            {
                _settings.Update(s => s.SubtitleFont = value);
            }
        }
    }

    public int SubtitleSize
    {
        get => _subtitleSize;
        set
        {
            if (SetProperty(ref _subtitleSize, value))
            {
                _settings.Update(s => s.SubtitleSize = value);
            }
        }
    }

    public string SubtitleColor
    {
        get => _subtitleColor;
        set
        {
            if (SetProperty(ref _subtitleColor, value))
            {
                _settings.Update(s => s.SubtitleColor = value);
            }
        }
    }

    public string SubtitleBackground
    {
        get => _subtitleBackground;
        set
        {
            if (SetProperty(ref _subtitleBackground, value))
            {
                _settings.Update(s => s.SubtitleBackground = value);
            }
        }
    }

    public string DefaultSubtitleLanguage
    {
        get => _defaultSubtitleLanguage;
        set
        {
            if (SetProperty(ref _defaultSubtitleLanguage, value))
            {
                _settings.Update(s => s.DefaultSubtitleLanguage = value);
            }
        }
    }

    public string DefaultAudioLanguage
    {
        get => _defaultAudioLanguage;
        set
        {
            if (SetProperty(ref _defaultAudioLanguage, value))
            {
                _settings.Update(s => s.DefaultAudioLanguage = value);
            }
        }
    }

    // Trakt Properties
    public bool IsTraktConnected => _trakt.IsConnected;

    public string? TraktUsername => _settings.Current.TraktUsername;

    public string? TraktAuthCode
    {
        get => _traktAuthCode;
        set => SetProperty(ref _traktAuthCode, value);
    }

    public string? TraktAuthUrl
    {
        get => _traktAuthUrl;
        set => SetProperty(ref _traktAuthUrl, value);
    }

    public bool TraktAutoScrobble
    {
        get => _traktAutoScrobble;
        set
        {
            if (SetProperty(ref _traktAutoScrobble, value))
            {
                _settings.Update(s => s.TraktAutoScrobble = value);
            }
        }
    }

    public bool TraktSyncWatchedState
    {
        get => _traktSyncWatchedState;
        set
        {
            if (SetProperty(ref _traktSyncWatchedState, value))
            {
                _settings.Update(s => s.TraktSyncWatchedState = value);
            }
        }
    }

    public bool TraktShowWatchlistInSidebar
    {
        get => _traktShowWatchlistInSidebar;
        set
        {
            if (SetProperty(ref _traktShowWatchlistInSidebar, value))
            {
                _settings.Update(s => s.TraktShowWatchlistInSidebar = value);
            }
        }
    }

    public string TraktClientId
    {
        get => _traktClientId;
        set
        {
            if (SetProperty(ref _traktClientId, value))
            {
                _settings.Update(s => s.TraktClientIdOverride = value);
            }
        }
    }

    public string TraktClientSecret
    {
        get => _traktClientSecret;
        set
        {
            if (SetProperty(ref _traktClientSecret, value))
            {
                _settings.Update(s => s.TraktClientSecretOverride = value);
            }
        }
    }

    public async Task StartTraktAuthAsync()
    {
        var dc = await _trakt.GetDeviceCodeAsync();
        if (dc != null)
        {
            TraktAuthCode = dc.UserCode;
            TraktAuthUrl = dc.VerificationUrl;

            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(dc.Interval > 0 ? dc.Interval : 5));
                    var success = await _trakt.PollTokenAsync(dc.DeviceCode);
                    if (success)
                    {
                        OnPropertyChanged(nameof(IsTraktConnected));
                        OnPropertyChanged(nameof(TraktUsername));
                        TraktAuthCode = null;
                        break;
                    }
                }
            });
        }
    }

    public void DisconnectTrakt()
    {
        _settings.Update(s =>
        {
            s.TraktAccessToken = null;
            s.TraktRefreshToken = null;
            s.TraktUsername = null;
            s.TraktTokenExpiresAt = null;
        });
        OnPropertyChanged(nameof(IsTraktConnected));
        OnPropertyChanged(nameof(TraktUsername));
    }

    // Diagnostics & Sync
    public string SyncStatusText
    {
        get => _syncStatusText;
        set => SetProperty(ref _syncStatusText, value);
    }

    public bool IsSyncing
    {
        get => _isSyncing;
        set => SetProperty(ref _isSyncing, value);
    }

    public async Task TriggerSyncAsync()
    {
        await _sync.RunFullSyncAsync();
        RefreshDatabaseStats();
    }

    public void ClearCache()
    {
        _db.ClearAllCache();
        RefreshDatabaseStats();
    }

    public void ApplyTheme(string theme)
    {
        try
        {
            switch (theme.ToLowerInvariant())
            {
                case "dark":
                    ApplicationThemeManager.Apply(ApplicationTheme.Dark);
                    break;
                case "light":
                    ApplicationThemeManager.Apply(ApplicationTheme.Light);
                    break;
                default:
                    ApplicationThemeManager.ApplySystemTheme();
                    break;
            }
        }
        catch { }
    }
}
