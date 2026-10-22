using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Nocturne.App.Services;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class DiscoveredConnectServerUi : BaseViewModel
{
    public ConnectServer Server { get; }
    public string Name => Server.Name ?? "Emby Server";
    public string Url => !string.IsNullOrEmpty(Server.Url) ? Server.Url : Server.LocalAddress ?? string.Empty;

    private bool _isAlreadyAdded;
    public bool IsAlreadyAdded
    {
        get => _isAlreadyAdded;
        set => SetProperty(ref _isAlreadyAdded, value);
    }

    private bool _isAdding;
    public bool IsAdding
    {
        get => _isAdding;
        set => SetProperty(ref _isAdding, value);
    }

    private bool _isAdded;
    public bool IsAdded
    {
        get => _isAdded;
        set => SetProperty(ref _isAdded, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public DiscoveredConnectServerUi(ConnectServer server, bool isAlreadyAdded)
    {
        Server = server;
        _isAlreadyAdded = isAlreadyAdded;
        _isAdded = isAlreadyAdded;
    }
}

public class ServerPriorityItem : BaseViewModel
{
    public ServerConfig Server { get; }
    public string Id => Server.Id;
    public string Name => Server.Name;
    public string Url => Server.Url;
    public string UserId => Server.UserId;
    public string Username => Server.Username;

    private int _priority;
    public int Priority
    {
        get => _priority;
        set
        {
            if (SetProperty(ref _priority, value))
            {
                OnPropertyChanged(nameof(PriorityBadgeText));
                OnPropertyChanged(nameof(CanMoveUp));
                OnPropertyChanged(nameof(CanMoveDown));
            }
        }
    }

    public string PriorityBadgeText => $"#{Priority} Priority";

    private int _totalServers;
    public int TotalServers
    {
        get => _totalServers;
        set
        {
            if (SetProperty(ref _totalServers, value))
            {
                OnPropertyChanged(nameof(CanMoveDown));
            }
        }
    }

    public bool CanMoveUp => Priority > 1;
    public bool CanMoveDown => Priority < TotalServers;

    public ServerPriorityItem(ServerConfig server, int priority, int totalServers)
    {
        Server = server;
        _priority = priority;
        _totalServers = totalServers;
    }
}

public class ServerSyncProgressItem : BaseViewModel
{
    public string ServerId { get; set; } = string.Empty;

    private string _serverName = string.Empty;
    public string ServerName
    {
        get => _serverName;
        set => SetProperty(ref _serverName, value);
    }

    private string _libraryName = string.Empty;
    public string LibraryName
    {
        get => _libraryName;
        set => SetProperty(ref _libraryName, value);
    }

    private int _currentItem;
    public int CurrentItem
    {
        get => _currentItem;
        set
        {
            if (SetProperty(ref _currentItem, value))
            {
                UpdateItemsProgressText();
            }
        }
    }

    private int _totalItems;
    public int TotalItems
    {
        get => _totalItems;
        set
        {
            if (SetProperty(ref _totalItems, value))
            {
                UpdateItemsProgressText();
            }
        }
    }

    private string _itemsProgressText = "Discovering...";
    public string ItemsProgressText
    {
        get => _itemsProgressText;
        private set => SetProperty(ref _itemsProgressText, value);
    }

    private int _batchSize;
    public int BatchSize
    {
        get => _batchSize;
        set
        {
            if (SetProperty(ref _batchSize, value))
            {
                OnPropertyChanged(nameof(BatchText));
            }
        }
    }

    public string BatchText => _batchSize > 0 ? $"{_batchSize:N0}" : "-";

    private long _latencyMs;
    public long LatencyMs
    {
        get => _latencyMs;
        set
        {
            if (SetProperty(ref _latencyMs, value))
            {
                OnPropertyChanged(nameof(LatencyText));
            }
        }
    }

    public string LatencyText => _latencyMs > 0 ? $"{_latencyMs}ms" : "-";

    private string _statusText = string.Empty;
    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private double _progressPercent;
    public double ProgressPercent
    {
        get => _progressPercent;
        set => SetProperty(ref _progressPercent, value);
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set => SetProperty(ref _isIndeterminate, value);
    }

    private bool _isComplete;
    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    private void UpdateItemsProgressText()
    {
        if (_totalItems > 0)
        {
            ItemsProgressText = $"{_currentItem:N0} / {_totalItems:N0} items";
        }
        else if (_currentItem > 0)
        {
            ItemsProgressText = $"{_currentItem:N0} items";
        }
        else
        {
            ItemsProgressText = "Discovering...";
        }
    }
}

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
    private readonly ITraktAuthService _traktAuth;
    private readonly ITraktSyncService _traktSync;
    private readonly ITmdbEnrichmentService _tmdbEnrichment;
    private readonly TraktQueueDrainWorker _traktDrain;
    private DateTime _lastDatabaseStatsRefreshUtc = DateTime.MinValue;

    // Categorized Tabs Navigation
    private string _selectedCategory = "servers";
    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                OnPropertyChanged(nameof(IsServersCategory));
                OnPropertyChanged(nameof(IsLibrariesCategory));
                OnPropertyChanged(nameof(IsPlaybackCategory));
                OnPropertyChanged(nameof(IsAppearanceCategory));
                OnPropertyChanged(nameof(IsIntegrationsCategory));
                OnPropertyChanged(nameof(IsAboutCategory));
                RefreshDatabaseStats();
            }
        }
    }

    public bool IsServersCategory => SelectedCategory == "servers";
    public bool IsLibrariesCategory => SelectedCategory == "libraries";
    public bool IsPlaybackCategory => SelectedCategory == "playback";
    public bool IsAppearanceCategory => SelectedCategory == "appearance";
    public bool IsIntegrationsCategory => SelectedCategory == "integrations";
    public bool IsAboutCategory => SelectedCategory == "about";

    public void SelectCategory(string category)
    {
        SelectedCategory = category;
    }

    // Server adding fields
    private string _newServerUrl = "http://";
    private string _newUsername = string.Empty;
    private string _newPassword = string.Empty;
    private string _serverAddError = string.Empty;
    private bool _isAddingServer;

    // Emby Connect linking fields
    private bool _isEmbyConnectActive;
    private bool _isConnectPinActive;
    private bool _isConnectServersListActive;
    private bool _isPollingConnectPin;
    private string _connectPin = string.Empty;
    private string _connectStatusMessage = string.Empty;
    private string? _connectErrorMessage;
    private string _connectUserId = string.Empty;
    private string _connectAccessToken = string.Empty;
    private bool _isAddingAllServers;
    private CancellationTokenSource? _connectPinCts;

    public ObservableCollection<DiscoveredConnectServerUi> DiscoveredServers { get; } = new();

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
    private double _syncProgressPercent;
    private bool _isSyncIndeterminate;
    private string _syncDetailedStatus = string.Empty;
    private string _cacheStatusMessage = string.Empty;
    public event Action? CacheCleared;
    private string? _traktAuthCode;
    private string? _traktAuthUrl;
    private bool _traktAutoScrobble = true;
    private bool _traktSyncWatchedState = true;
    private bool _traktShowWatchlistInSidebar = true;
    private string _traktClientId = string.Empty;

    public ObservableCollection<ServerPriorityItem> Servers { get; } = new();
    public ObservableCollection<CombinedMapping> CombinedGroups { get; } = new();

    public SettingsViewModel(
        SettingsService settings,
        EmbyClient embyClient,
        DatabaseService db,
        DedupEngine dedup,
        SyncEngine sync,
        TraktService trakt,
        ThemeColorService themeColors,
        EmbyConnectService connect,
        ITraktAuthService traktAuth,
        ITraktSyncService traktSync,
        ITmdbEnrichmentService tmdbEnrichment,
        TraktQueueDrainWorker traktDrain)
    {
        _settings = settings;
        _embyClient = embyClient;
        _db = db;
        _dedup = dedup;
        _sync = sync;
        _trakt = trakt;
        _themeColors = themeColors;
        _connect = connect;
        _traktAuth = traktAuth;
        _traktSync = traktSync;
        _tmdbEnrichment = tmdbEnrichment;
        _traktDrain = traktDrain;

        _traktAuth.StateChanged += (s, e) =>
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                OnPropertyChanged(nameof(IsTraktConnected));
                OnPropertyChanged(nameof(TraktStateText));
                OnPropertyChanged(nameof(TraktUsername));
                OnPropertyChanged(nameof(TraktQueueDepth));
            });
        };

        LoadFromSettings();

        _sync.ProgressReported += report =>
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                SyncDetailedStatus = report.StatusMessage;
                SyncProgressPercent = report.Percentage;
                IsSyncIndeterminate = report.State == SyncState.Discovering;

                // Live-update cached items & dedup groups count during active sync
                if (report.State == SyncState.Writing || report.State == SyncState.Fetching || report.State == SyncState.Deduping)
                {
                    if ((DateTime.UtcNow - _lastDatabaseStatsRefreshUtc).TotalSeconds >= 1.5)
                    {
                        _lastDatabaseStatsRefreshUtc = DateTime.UtcNow;
                        RefreshDatabaseStats();
                    }
                }

                if (!string.IsNullOrEmpty(report.ServerId) && report.ServerId != "catalog" && report.ServerId != "trakt")
                {
                    var existing = ActiveServerProgresses.FirstOrDefault(p => p.ServerId == report.ServerId);
                    if (existing == null)
                    {
                        existing = new ServerSyncProgressItem
                        {
                            ServerId = report.ServerId,
                            ServerName = !string.IsNullOrEmpty(report.ServerName) ? report.ServerName : "Server",
                            LibraryName = report.LibraryName,
                            CurrentItem = report.CurrentItem,
                            TotalItems = report.TotalItems,
                            BatchSize = report.BatchSize,
                            LatencyMs = report.LatencyMs,
                            StatusText = report.StatusMessage,
                            ProgressPercent = Math.Clamp(report.ServerPercentage, 0, 100),
                            IsIndeterminate = report.State == SyncState.Discovering,
                            IsComplete = report.ServerPercentage >= 100.0
                        };
                        ActiveServerProgresses.Add(existing);
                        UpdateSyncHeaderProperties();
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(report.ServerName) && existing.ServerName != report.ServerName)
                        {
                            existing.ServerName = report.ServerName;
                            UpdateSyncHeaderProperties();
                        }
                        if (!string.IsNullOrEmpty(report.LibraryName)) existing.LibraryName = report.LibraryName;
                        if (report.CurrentItem > 0 || report.TotalItems > 0)
                        {
                            existing.CurrentItem = report.CurrentItem;
                            existing.TotalItems = report.TotalItems;
                        }
                        if (report.BatchSize > 0) existing.BatchSize = report.BatchSize;
                        if (report.LatencyMs > 0) existing.LatencyMs = report.LatencyMs;
                        existing.StatusText = report.StatusMessage;
                        existing.ProgressPercent = Math.Clamp(report.ServerPercentage, 0, 100);
                        existing.IsIndeterminate = report.State == SyncState.Discovering;
                        if (report.ServerPercentage >= 100.0)
                        {
                            existing.IsComplete = true;
                        }
                    }
                }
                else if (report.State == SyncState.Deduping || report.ServerId == "catalog" || report.ServerId == "trakt")
                {
                    HasGlobalProgress = true;
                    GlobalSyncStatusText = report.StatusMessage;
                    GlobalSyncProgressPercent = report.Percentage;
                }
            });
        };

        _sync.StateChanged += (state, msg) =>
        {
            System.Windows.Application.Current?.Dispatcher?.Invoke(() =>
            {
                SyncStatusText = msg;
                IsSyncing = state != SyncState.Idle && state != SyncState.Done && state != SyncState.Error;
                if (state == SyncState.Discovering)
                {
                    ActiveServerProgresses.Clear();
                    var srvs = _settings.Current.Servers.ToList();
                    if (srvs.Count == 0 && _embyClient.IsAuthenticated)
                    {
                        srvs.Add(new ServerConfig
                        {
                            Id = _settings.Current.ActiveServerId ?? "primary",
                            Name = "Emby Server",
                            Url = _embyClient.BaseUrl
                        });
                    }

                    foreach (var srv in srvs)
                    {
                        ActiveServerProgresses.Add(new ServerSyncProgressItem
                        {
                            ServerId = srv.Id,
                            ServerName = srv.Name,
                            StatusText = "Connecting & discovering libraries...",
                            ProgressPercent = 0,
                            IsIndeterminate = true
                        });
                    }
                    UpdateSyncHeaderProperties();
                    HasGlobalProgress = false;
                }
                else if (state == SyncState.Done || state == SyncState.Error || state == SyncState.Idle)
                {
                    RefreshDatabaseStats();
                    HasGlobalProgress = false;
                    UpdateSyncHeaderProperties();
                }
            });
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

        _subtitleFont = CleanFontName(s.SubtitleFont);
        _subtitleSize = s.SubtitleSize;
        _subtitleColor = s.SubtitleColor;
        _subtitleBackground = s.SubtitleBackground;
        _subtitleBorder = s.SubtitleBorder;
        _defaultSubtitleLanguage = s.DefaultSubtitleLanguage;
        _defaultAudioLanguage = s.DefaultAudioLanguage;

        _libraryMode = s.LibraryMode;
        _showUnmappedLibraries = s.ShowUnmappedLibraries;
        _dedupEnabled = s.DedupEnabled;

        _traktAutoScrobble = s.TraktAutoScrobble;
        _traktSyncWatchedState = s.TraktSyncWatchedState;
        _traktShowWatchlistInSidebar = s.TraktShowWatchlistInSidebar;
        _traktClientId = _traktAuth.CustomClientId ?? string.Empty;

        LoadServers();
        LoadCombinedGroups();
    }

    public static string CleanFontName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Segoe UI Variable Text";
        var clean = raw.Replace("System.Windows.Controls.ComboBoxItem: ", "").Trim();
        return string.IsNullOrEmpty(clean) ? "Segoe UI Variable Text" : clean;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double dblBytes = bytes;
        while (dblBytes >= 1024.0 && i < suffixes.Length - 1)
        {
            dblBytes /= 1024.0;
            i++;
        }
        return $"{dblBytes:0.##} {suffixes[i]}";
    }

    public void LoadServers()
    {
        Servers.Clear();
        var priorityList = _settings.Current.PreferredServerPriority ?? new List<string>();
        var allServers = _settings.Current.Servers.ToList();

        var ordered = allServers.OrderBy(srv =>
        {
            var idx = priorityList.IndexOf(srv.Id);
            return idx >= 0 ? idx : int.MaxValue;
        }).ToList();

        int total = ordered.Count;
        for (int i = 0; i < total; i++)
        {
            Servers.Add(new ServerPriorityItem(ordered[i], i + 1, total));
        }
    }

    public void MoveServerUp(string serverId)
    {
        var item = Servers.FirstOrDefault(s => s.Id == serverId);
        if (item == null) return;
        int idx = Servers.IndexOf(item);
        if (idx > 0)
        {
            MoveServer(idx, idx - 1);
        }
    }

    public void MoveServerDown(string serverId)
    {
        var item = Servers.FirstOrDefault(s => s.Id == serverId);
        if (item == null) return;
        int idx = Servers.IndexOf(item);
        if (idx >= 0 && idx < Servers.Count - 1)
        {
            MoveServer(idx, idx + 1);
        }
    }

    public void MoveServer(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || oldIndex >= Servers.Count || newIndex < 0 || newIndex >= Servers.Count || oldIndex == newIndex)
            return;

        var item = Servers[oldIndex];
        Servers.RemoveAt(oldIndex);
        Servers.Insert(newIndex, item);

        for (int i = 0; i < Servers.Count; i++)
        {
            Servers[i].Priority = i + 1;
            Servers[i].TotalServers = Servers.Count;
        }

        var newPriorityList = Servers.Select(s => s.Id).ToList();
        _settings.Update(s =>
        {
            s.PreferredServerPriority = newPriorityList;
            s.Servers = s.Servers.OrderBy(srv =>
            {
                var idx = newPriorityList.IndexOf(srv.Id);
                return idx >= 0 ? idx : int.MaxValue;
            }).ToList();
        });

        _sync.SetServerPriority(newPriorityList);
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
            DatabaseSizeText = FormatBytes(sizeBytes);
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
                    if (!s.PreferredServerPriority.Contains(newServer.Id))
                    {
                        s.PreferredServerPriority.Add(newServer.Id);
                    }
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
            s.PreferredServerPriority.Remove(serverId);
            if (s.ActiveServerId == serverId)
            {
                s.ActiveServerId = s.Servers.FirstOrDefault()?.Id;
            }
        });

        _db.ClearServerData(serverId);
        LoadServers();
        OnPropertyChanged(nameof(ActiveServerId));
    }

    // Emby Connect Properties & Methods
    public bool IsEmbyConnectActive
    {
        get => _isEmbyConnectActive;
        set => SetProperty(ref _isEmbyConnectActive, value);
    }

    public bool IsConnectPinActive
    {
        get => _isConnectPinActive;
        set => SetProperty(ref _isConnectPinActive, value);
    }

    public bool IsConnectServersListActive
    {
        get => _isConnectServersListActive;
        set => SetProperty(ref _isConnectServersListActive, value);
    }

    public bool IsPollingConnectPin
    {
        get => _isPollingConnectPin;
        set => SetProperty(ref _isPollingConnectPin, value);
    }

    public string ConnectPin
    {
        get => _connectPin;
        set => SetProperty(ref _connectPin, value);
    }

    public string ConnectStatusMessage
    {
        get => _connectStatusMessage;
        set => SetProperty(ref _connectStatusMessage, value);
    }

    public string? ConnectErrorMessage
    {
        get => _connectErrorMessage;
        set => SetProperty(ref _connectErrorMessage, value);
    }

    public bool IsAddingAllServers
    {
        get => _isAddingAllServers;
        set => SetProperty(ref _isAddingAllServers, value);
    }

    public async Task StartEmbyConnectFlowAsync()
    {
        ConnectErrorMessage = null;
        ConnectStatusMessage = "Requesting PIN code from Emby Connect...";
        IsEmbyConnectActive = true;
        IsConnectPinActive = false;
        IsConnectServersListActive = false;
        DiscoveredServers.Clear();

        _connectPinCts?.Cancel();
        _connectPinCts = new CancellationTokenSource();

        try
        {
            var pinRes = await _connect.CreatePinAsync();
            if (pinRes == null || string.IsNullOrEmpty(pinRes.Pin))
            {
                ConnectErrorMessage = "Could not generate PIN code. Please ensure you are connected to the internet.";
                return;
            }

            ConnectPin = pinRes.Pin;
            IsConnectPinActive = true;
            IsPollingConnectPin = true;
            ConnectStatusMessage = "Waiting for confirmation at emby.media/pin...";

            var pin = pinRes.Pin;
            var token = _connectPinCts.Token;

            _ = Task.Run(async () =>
            {
                var stopwatch = Stopwatch.StartNew();
                while (!token.IsCancellationRequested && stopwatch.Elapsed < TimeSpan.FromMinutes(10))
                {
                    try
                    {
                        await Task.Delay(2000, token);
                    }
                    catch
                    {
                        break;
                    }

                    if (token.IsCancellationRequested) break;

                    var status = await _connect.CheckPinStatusAsync(pin);
                    if (status != null && status.IsConfirmed)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            await HandleConnectPinConfirmedAsync(pin);
                        });
                        break;
                    }

                    if (status != null && status.IsExpired)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ConnectErrorMessage = "The PIN code has expired. Please try again.";
                            IsConnectPinActive = false;
                            IsPollingConnectPin = false;
                        });
                        break;
                    }
                }
            }, token);
        }
        catch (Exception ex)
        {
            ConnectErrorMessage = $"Error starting Emby Connect: {ex.Message}";
        }
    }

    private async Task HandleConnectPinConfirmedAsync(string pin)
    {
        IsPollingConnectPin = false;
        IsConnectPinActive = false;
        ConnectStatusMessage = "PIN confirmed! Fetching your Emby servers...";

        try
        {
            var exchange = await _connect.ExchangePinAsync(pin);
            if (exchange == null || string.IsNullOrEmpty(exchange.AccessToken) || string.IsNullOrEmpty(exchange.UserId))
            {
                ConnectErrorMessage = "Failed to finalize Emby Connect authorization.";
                return;
            }

            _connectUserId = exchange.UserId;
            _connectAccessToken = exchange.AccessToken;

            var servers = await _connect.GetServersAsync(exchange.UserId, exchange.AccessToken);
            if (servers.Count == 0)
            {
                ConnectErrorMessage = "No Emby servers found on this Emby Connect account.";
                return;
            }

            var existing = _settings.Current.Servers;
            DiscoveredServers.Clear();
            foreach (var s in servers)
            {
                var isAlready = existing.Any(e =>
                    (!string.IsNullOrEmpty(e.Id) && !string.IsNullOrEmpty(s.SystemId) && e.Id.Equals(s.SystemId, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.Url) && !string.IsNullOrEmpty(s.Url) && e.Url.TrimEnd('/').Equals(s.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(e.Url) && !string.IsNullOrEmpty(s.LocalAddress) && e.Url.TrimEnd('/').Equals(s.LocalAddress.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));

                DiscoveredServers.Add(new DiscoveredConnectServerUi(s, isAlready));
            }

            IsConnectServersListActive = true;
            ConnectStatusMessage = $"Found {servers.Count} server(s) on your Emby Connect account.";
        }
        catch (Exception ex)
        {
            ConnectErrorMessage = $"Failed to fetch servers: {ex.Message}";
        }
    }

    public async Task AddAllConnectServersAsync()
    {
        if (string.IsNullOrEmpty(_connectUserId) || DiscoveredServers.Count == 0) return;

        IsAddingAllServers = true;
        ConnectStatusMessage = "Connecting and adding servers...";
        int addedCount = 0;

        foreach (var item in DiscoveredServers)
        {
            if (item.IsAdded) continue;

            item.IsAdding = true;
            try
            {
                var (tok, uid, resolvedUrl) = await _connect.ConnectToServerFastAsync(item.Server, _connectUserId);
                if (!string.IsNullOrEmpty(tok) && !string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(resolvedUrl))
                {
                    var info = await _embyClient.GetServerInfoAsync(resolvedUrl);
                    var serverName = info?.ServerName ?? item.Server.Name ?? "Emby Server";
                    var serverVer = info?.Version ?? item.Server.SystemId ?? "4.x";

                    var newServer = new ServerConfig
                    {
                        Id = !string.IsNullOrEmpty(item.Server.SystemId) ? item.Server.SystemId : Guid.NewGuid().ToString("N")[..8],
                        Name = serverName,
                        Url = resolvedUrl,
                        UserId = uid,
                        Username = serverName,
                        AccessToken = tok,
                        Version = serverVer,
                        AddedAt = DateTime.UtcNow,
                        LastConnected = DateTime.UtcNow
                    };

                    _settings.Update(s =>
                    {
                        s.Servers.RemoveAll(x => (x.Id == newServer.Id) || (x.Url == newServer.Url && x.UserId == newServer.UserId));
                        s.Servers.Add(newServer);
                        if (!s.PreferredServerPriority.Contains(newServer.Id))
                        {
                            s.PreferredServerPriority.Add(newServer.Id);
                        }
                        if (string.IsNullOrEmpty(s.ActiveServerId))
                        {
                            s.ActiveServerId = newServer.Id;
                        }
                    });

                    item.IsAdded = true;
                    item.IsAlreadyAdded = true;
                    addedCount++;
                }
                else
                {
                    item.ErrorMessage = "Connection failed";
                }
            }
            catch (Exception ex)
            {
                item.ErrorMessage = ex.Message;
            }
            finally
            {
                item.IsAdding = false;
            }
        }

        LoadServers();
        IsAddingAllServers = false;

        if (addedCount > 0)
        {
            ConnectStatusMessage = $"Successfully added {addedCount} server(s) from Emby Connect!";
            _ = _sync.SyncAllAsync();
        }
        else
        {
            ConnectStatusMessage = "All available servers were already added or could not be reached.";
        }
    }

    public async Task AddSingleConnectServerAsync(DiscoveredConnectServerUi item)
    {
        if (item == null || item.IsAdded || string.IsNullOrEmpty(_connectUserId)) return;

        item.IsAdding = true;
        ConnectStatusMessage = $"Connecting to {item.Name}...";

        try
        {
            var (tok, uid, resolvedUrl) = await _connect.ConnectToServerFastAsync(item.Server, _connectUserId);
            if (!string.IsNullOrEmpty(tok) && !string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(resolvedUrl))
            {
                var info = await _embyClient.GetServerInfoAsync(resolvedUrl);
                var serverName = info?.ServerName ?? item.Server.Name ?? "Emby Server";
                var serverVer = info?.Version ?? item.Server.SystemId ?? "4.x";

                var newServer = new ServerConfig
                {
                    Id = !string.IsNullOrEmpty(item.Server.SystemId) ? item.Server.SystemId : Guid.NewGuid().ToString("N")[..8],
                    Name = serverName,
                    Url = resolvedUrl,
                    UserId = uid,
                    Username = serverName,
                    AccessToken = tok,
                    Version = serverVer,
                    AddedAt = DateTime.UtcNow,
                    LastConnected = DateTime.UtcNow
                };

                _settings.Update(s =>
                {
                    s.Servers.RemoveAll(x => (x.Id == newServer.Id) || (x.Url == newServer.Url && x.UserId == newServer.UserId));
                    s.Servers.Add(newServer);
                    if (!s.PreferredServerPriority.Contains(newServer.Id))
                    {
                        s.PreferredServerPriority.Add(newServer.Id);
                    }
                    if (string.IsNullOrEmpty(s.ActiveServerId))
                    {
                        s.ActiveServerId = newServer.Id;
                    }
                });

                item.IsAdded = true;
                item.IsAlreadyAdded = true;
                LoadServers();
                ConnectStatusMessage = $"Added {serverName} successfully!";
                _ = _sync.SyncAllAsync();
            }
            else
            {
                item.ErrorMessage = "Connection failed";
                ConnectStatusMessage = $"Failed to connect to {item.Name}.";
            }
        }
        catch (Exception ex)
        {
            item.ErrorMessage = ex.Message;
            ConnectStatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            item.IsAdding = false;
        }
    }

    public void CancelEmbyConnectFlow()
    {
        _connectPinCts?.Cancel();
        IsPollingConnectPin = false;
        IsConnectPinActive = false;
        IsConnectServersListActive = false;
        IsEmbyConnectActive = false;
        ConnectErrorMessage = null;
        ConnectStatusMessage = string.Empty;
    }

    public void CloseConnectFlow()
    {
        CancelEmbyConnectFlow();
    }

    public void OpenPinBrowser()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://emby.media/pin",
                UseShellExecute = true
            });
        }
        catch { }
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
                OnPropertyChanged(nameof(IsSeparateMode));
                OnPropertyChanged(nameof(IsCombinedMode));
            }
        }
    }

    public bool IsSeparateMode => _libraryMode != "combined";
    public bool IsCombinedMode => _libraryMode == "combined";

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
        SyncStatusText = FeatureFlags.DedupAlert(_db) ?? "Deduplication index rebuild complete.";
    }

    public async Task RunDeletionSweepAsync()
    {
        IsSyncing = true;
        SyncStatusText = "Checking every library for deleted items...";
        try
        {
            var r = await _sync.RunDeletionSweepAsync();
            SyncStatusText = $"Checked {r.Libraries} libraries: removed {r.Removed:N0} deleted item(s)" +
                             (r.Skipped > 0 ? $"; {r.Skipped} skipped (listing failed or too much missing, see the log)." : ".");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("SettingsViewModel", "Deletion sweep failed", ex);
            SyncStatusText = $"Deleted-item check failed: {ex.Message}";
        }
        RefreshDatabaseStats();
        IsSyncing = false;
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
            var clean = CleanFontName(value);
            if (SetProperty(ref _subtitleFont, clean))
            {
                _settings.Update(s => s.SubtitleFont = clean);
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

    private string _subtitleBorder = "none";
    public string SubtitleBorder
    {
        get => _subtitleBorder;
        set
        {
            if (SetProperty(ref _subtitleBorder, value))
            {
                _settings.Update(s => s.SubtitleBorder = value);
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
    public bool IsTraktConnected => _traktAuth.State == TraktConnectionState.Connected;
    public string TraktStateText => _traktAuth.State.ToString();
    public string? TraktUsername => _traktAuth.Username ?? _settings.Current.TraktUsername;
    public int TraktQueueDepth => _traktDrain.PendingQueueDepth;

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
            // I2: the auth service that connects and scrobbles uses this ID. It was written to the legacy settings
            // field, which nothing on the live path read. Changing it invalidates tokens minted with the old ID.
            if (SetProperty(ref _traktClientId, value))
            {
                _traktAuth.SetCustomClientId(value);
                OnPropertyChanged(nameof(IsTraktConnected));
                OnPropertyChanged(nameof(TraktStateText));
            }
        }
    }

    private string? _traktStatusMessage;
    public string? TraktStatusMessage
    {
        get => _traktStatusMessage;
        set => SetProperty(ref _traktStatusMessage, value);
    }

    public void OpenTraktAppCreationUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://trakt.tv/oauth/applications/new",
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void OpenTraktManageAppsUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://trakt.tv/settings/apps",
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void OpenTraktActivationUrl()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://trakt.tv/activate",
                UseShellExecute = true
            });
        }
        catch { }
    }

    public async Task StartTraktAuthAsync()
    {
        TraktStatusMessage = "Connecting to Trakt.tv...";
        try
        {
            var dc = await _traktAuth.RequestDeviceCodeAsync(CancellationToken.None);
            TraktAuthCode = dc.UserCode;
            TraktAuthUrl = dc.VerificationUrl;
            TraktStatusMessage = $"Code generated: {dc.UserCode}. Opening trakt.tv/activate...";

            // Auto-copy code to clipboard
            try
            {
                Clipboard.SetText(dc.UserCode);
            }
            catch { }

            // Auto-open browser to activate
            OpenTraktActivationUrl();

            _ = Task.Run(async () =>
            {
                var deadline = DateTime.UtcNow.AddSeconds(dc.ExpiresIn);
                while (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(TimeSpan.FromSeconds(dc.Interval > 0 ? dc.Interval : 5));
                    var res = await _traktAuth.PollForTokenAsync(dc, CancellationToken.None);
                    if (res.Status == TraktAuthStatus.Authorized)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(async () =>
                        {
                            OnPropertyChanged(nameof(IsTraktConnected));
                            OnPropertyChanged(nameof(TraktStateText));
                            OnPropertyChanged(nameof(TraktUsername));
                            TraktAuthCode = null;
                            TraktStatusMessage = $"Connected successfully as @{_traktAuth.Username}!";
                            _ = _sync.RunFullSyncAsync();
                        });
                        break;
                    }
                    if (res.Status == TraktAuthStatus.Expired || res.Status == TraktAuthStatus.Denied || res.Status == TraktAuthStatus.AlreadyUsed)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            TraktAuthCode = null;
                            TraktStatusMessage = res.ErrorMessage ?? $"Authorization {res.Status}. Please click Connect Trakt again.";
                        });
                        break;
                    }
                }
            });
        }
        catch (Exception ex)
        {
            TraktStatusMessage = $"Failed to initiate Trakt authorization: {ex.Message}";
        }
    }

    public async Task DisconnectTraktAsync()
    {
        await _traktAuth.DisconnectAsync(CancellationToken.None);
        _settings.Update(s =>
        {
            s.TraktAccessToken = null;
            s.TraktRefreshToken = null;
            s.TraktUsername = null;
            s.TraktTokenExpiresAt = null;
        });
        TraktStatusMessage = "Disconnected from Trakt.tv.";
        OnPropertyChanged(nameof(IsTraktConnected));
        OnPropertyChanged(nameof(TraktStateText));
        OnPropertyChanged(nameof(TraktUsername));
    }

    public void DisconnectTrakt()
    {
        _ = DisconnectTraktAsync();
    }

    private bool _isTraktReconciling;
    public bool IsTraktReconciling
    {
        get => _isTraktReconciling;
        set => SetProperty(ref _isTraktReconciling, value);
    }

    public async Task RunTraktFullReconcileAsync()
    {
        if (IsTraktReconciling) return;
        IsTraktReconciling = true;
        TraktStatusMessage = "Running full reconcile with Trakt.tv...";
        try
        {
            var p = new Progress<int>(pct =>
            {
                TraktStatusMessage = $"Reconciling with Trakt: {pct}%";
            });
            await _traktSync.FullReconcileAsync(p, CancellationToken.None);
            TraktStatusMessage = "Trakt full reconcile complete.";
        }
        catch (Exception ex)
        {
            TraktStatusMessage = $"Full reconcile error: {ex.Message}";
        }
        finally
        {
            IsTraktReconciling = false;
        }
    }

    // TMDB Properties
    public int TmdbEnrichedCount => _tmdbEnrichment.EnrichedCount;
    public int TmdbPendingCount => _tmdbEnrichment.PendingCount;
    public int TmdbNotFoundCount => _tmdbEnrichment.NotFoundCount;

    public string TmdbApiKey
    {
        get => _tmdbEnrichment.GetActiveApiKey();
        set
        {
            _tmdbEnrichment.SetCustomApiKey(value);
            OnPropertyChanged(nameof(TmdbApiKey));
        }
    }

    private bool _isTmdbEnriching;
    public bool IsTmdbEnriching
    {
        get => _isTmdbEnriching;
        set => SetProperty(ref _isTmdbEnriching, value);
    }

    private string _tmdbEnrichStatus = string.Empty;
    public string TmdbEnrichStatus
    {
        get => _tmdbEnrichStatus;
        set => SetProperty(ref _tmdbEnrichStatus, value);
    }

    public async Task RunTmdbEnrichmentAsync()
    {
        if (IsTmdbEnriching) return;
        IsTmdbEnriching = true;
        TmdbEnrichStatus = "Starting TMDB enrichment...";
        try
        {
            var p = new Progress<EnrichProgress>(prog =>
            {
                TmdbEnrichStatus = $"Enriched {prog.Enriched}/{prog.Total} items ({prog.CurrentTitle})...";
                OnPropertyChanged(nameof(TmdbEnrichedCount));
                OnPropertyChanged(nameof(TmdbPendingCount));
                OnPropertyChanged(nameof(TmdbNotFoundCount));
            });
            await _tmdbEnrichment.RunAsync(p, CancellationToken.None);
            TmdbEnrichStatus = "TMDB enrichment finished.";
        }
        catch (Exception ex)
        {
            TmdbEnrichStatus = $"TMDB enrichment error: {ex.Message}";
        }
        finally
        {
            IsTmdbEnriching = false;
            OnPropertyChanged(nameof(TmdbEnrichedCount));
            OnPropertyChanged(nameof(TmdbPendingCount));
            OnPropertyChanged(nameof(TmdbNotFoundCount));
        }
    }

    public async Task AutoMapCombinedGroupsAsync()
    {
        IsSyncing = true;
        SyncStatusText = "Scanning all servers and auto-mapping combined libraries...";
        await _sync.RunFullSyncAsync(forceResync: true);
        LoadCombinedGroups();
        RefreshDatabaseStats();
        IsSyncing = false;
        SyncStatusText = "Auto-mapped combined libraries successfully.";
    }

    public ObservableCollection<ServerSyncProgressItem> ActiveServerProgresses { get; } = new();

    private bool _isSyncDetailsExpanded;
    public bool IsSyncDetailsExpanded
    {
        get => _isSyncDetailsExpanded;
        set => SetProperty(ref _isSyncDetailsExpanded, value);
    }

    public void ToggleSyncDetails()
    {
        IsSyncDetailsExpanded = !IsSyncDetailsExpanded;
    }

    public bool IsMultiServerSync => ActiveServerProgresses.Count > 1;
    public bool IsSingleServerSync => ActiveServerProgresses.Count <= 1;
    public ServerSyncProgressItem? PrimaryServerProgress => ActiveServerProgresses.FirstOrDefault();

    public string MasterSyncTitle
    {
        get
        {
            if (ActiveServerProgresses.Count > 1)
                return $"Syncing {ActiveServerProgresses.Count} Servers";
            if (ActiveServerProgresses.Count == 1)
                return $"Syncing {PrimaryServerProgress?.ServerName ?? "Server"}";
            return "Syncing...";
        }
    }

    public string MasterSyncSubtitle
    {
        get
        {
            if (ActiveServerProgresses.Count > 1)
                return "Cumulative progress";
            return "Entire library sync";
        }
    }

    public void UpdateSyncHeaderProperties()
    {
        OnPropertyChanged(nameof(IsMultiServerSync));
        OnPropertyChanged(nameof(IsSingleServerSync));
        OnPropertyChanged(nameof(PrimaryServerProgress));
        OnPropertyChanged(nameof(MasterSyncTitle));
        OnPropertyChanged(nameof(MasterSyncSubtitle));
    }

    private string _globalSyncStatusText = string.Empty;
    public string GlobalSyncStatusText
    {
        get => _globalSyncStatusText;
        set => SetProperty(ref _globalSyncStatusText, value);
    }

    private double _globalSyncProgressPercent;
    public double GlobalSyncProgressPercent
    {
        get => _globalSyncProgressPercent;
        set => SetProperty(ref _globalSyncProgressPercent, value);
    }

    private bool _hasGlobalProgress;
    public bool HasGlobalProgress
    {
        get => _hasGlobalProgress;
        set => SetProperty(ref _hasGlobalProgress, value);
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

    public double SyncProgressPercent
    {
        get => _syncProgressPercent;
        set => SetProperty(ref _syncProgressPercent, value);
    }

    public bool IsSyncIndeterminate
    {
        get => _isSyncIndeterminate;
        set => SetProperty(ref _isSyncIndeterminate, value);
    }

    public string SyncDetailedStatus
    {
        get => _syncDetailedStatus;
        set => SetProperty(ref _syncDetailedStatus, value);
    }

    public string CacheStatusMessage
    {
        get => _cacheStatusMessage;
        set => SetProperty(ref _cacheStatusMessage, value);
    }

    public async Task TriggerSyncAsync()
    {
        CacheStatusMessage = string.Empty;
        await _sync.RunFullSyncAsync(forceResync: true);
        RefreshDatabaseStats();
    }

    public void ClearCache()
    {
        try
        {
            _db.ClearAllCache();
            RefreshDatabaseStats();
            CacheStatusMessage = $"Cache cleared successfully. Storage reset to {DatabaseSizeText}.";
            CacheCleared?.Invoke();
        }
        catch (Exception ex)
        {
            CacheStatusMessage = $"Failed to clear cache: {ex.Message}";
        }
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
            UpdateThemeGradients();
            _themeColors.UpdateAdaptiveTextColors();
        }
        catch { }
    }

    public static void UpdateThemeGradients()
    {
        try
        {
            if (Application.Current == null) return;
            Application.Current.Dispatcher.Invoke(() =>
            {
                var appTheme = ApplicationThemeManager.GetAppTheme();
                bool isLight = appTheme == ApplicationTheme.Light;

                Color startColor = isLight ? Color.FromArgb(0xEE, 0xFA, 0xFA, 0xFC) : Color.FromArgb(0xC8, 0x0A, 0x0A, 0x0E);
                Color endColor = isLight ? Color.FromArgb(0x00, 0xFA, 0xFA, 0xFC) : Color.FromArgb(0x00, 0x0A, 0x0A, 0x0E);

                var leftBrush = new LinearGradientBrush(startColor, endColor, new Point(0, 0.5), new Point(1, 0.5));
                leftBrush.Freeze();

                var rightBrush = new LinearGradientBrush(endColor, startColor, new Point(0, 0.5), new Point(1, 0.5));
                rightBrush.Freeze();

                Application.Current.Resources["ScrollEdgeLeftBrush"] = leftBrush;
                Application.Current.Resources["ScrollEdgeRightBrush"] = rightBrush;
            });
        }
        catch { }
    }

    // ── About & Logging ──────────────────────────────────────────────────
    public string AppVersion => "v4.0.0 (Native Windows Edition)";
    public string RuntimeInfo => $".NET {Environment.Version} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})";
    public string LogsDirectoryPath => Nocturne.Core.Utils.AppPaths.DataDirectory;

    public void OpenLogsFolder()
    {
        try
        {
            var dir = LogsDirectoryPath;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public void OpenMainLog()
    {
        try
        {
            var path = Path.Combine(LogsDirectoryPath, "nocturne.log");
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else
            {
                OpenLogsFolder();
            }
        }
        catch { }
    }

    public void OpenCrashLog()
    {
        try
        {
            var path = Path.Combine(LogsDirectoryPath, "crash.log");
            if (File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            else
            {
                OpenLogsFolder();
            }
        }
        catch { }
    }
}
