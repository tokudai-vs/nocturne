using System.Collections.ObjectModel;
using System.Windows.Input;
using Nocturne.Core.Models;
using Nocturne.Core.Services;

namespace Nocturne.App.ViewModels;

public class OnboardingViewModel : BaseViewModel
{
    private readonly EmbyClient _emby;
    private readonly SettingsService _settings;
    private readonly SyncEngine _sync;

    private string _serverUrl = "http://";
    private bool _isConnecting;
    private string _errorMessage = string.Empty;
    private bool _isUserSelectionStep;
    private string _serverName = string.Empty;
    private string _serverVersion = string.Empty;
    private PublicUser? _selectedUser;
    private string _password = string.Empty;
    private bool _isManualMode;
    private string _manualUsername = string.Empty;

    public event Action? Authenticated;

    public ObservableCollection<ServerConfig> SavedServers { get; } = new();
    public ObservableCollection<PublicUser> PublicUsers { get; } = new();

    public OnboardingViewModel(EmbyClient emby, SettingsService settings, SyncEngine sync)
    {
        _emby = emby;
        _settings = settings;
        _sync = sync;

        LoadSavedServers();
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        set => SetProperty(ref _isConnecting, value);
    }

    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool IsUserSelectionStep
    {
        get => _isUserSelectionStep;
        set => SetProperty(ref _isUserSelectionStep, value);
    }

    public string ServerName
    {
        get => _serverName;
        set => SetProperty(ref _serverName, value);
    }

    public string ServerVersion
    {
        get => _serverVersion;
        set => SetProperty(ref _serverVersion, value);
    }

    public PublicUser? SelectedUser
    {
        get => _selectedUser;
        set => SetProperty(ref _selectedUser, value);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public bool IsManualMode
    {
        get => _isManualMode;
        set => SetProperty(ref _isManualMode, value);
    }

    public string ManualUsername
    {
        get => _manualUsername;
        set => SetProperty(ref _manualUsername, value);
    }

    public void LoadSavedServers()
    {
        SavedServers.Clear();
        foreach (var s in _settings.Current.Servers)
        {
            SavedServers.Add(s);
        }
    }

    public async Task ConnectToServerAsync()
    {
        ErrorMessage = string.Empty;
        var cleanUrl = EmbyClient.NormalizeServerUrl(ServerUrl);
        if (string.IsNullOrWhiteSpace(cleanUrl))
        {
            ErrorMessage = "Please enter an Emby server address.";
            return;
        }

        IsConnecting = true;
        try
        {
            var info = await _emby.GetServerInfoAsync(cleanUrl);
            if (info == null)
            {
                ErrorMessage = "Could not connect to Emby server. Please check the address and ensure the server is online.";
                IsConnecting = false;
                return;
            }

            ServerName = info.ServerName ?? "Emby Server";
            ServerVersion = info.Version ?? "4.x";

            // Query public users
            var users = await _emby.GetPublicUsersAsync(cleanUrl);
            PublicUsers.Clear();
            foreach (var u in users)
            {
                PublicUsers.Add(u);
            }

            IsUserSelectionStep = true;
            IsManualMode = PublicUsers.Count == 0;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection error: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task SelectUserAsync(PublicUser user)
    {
        SelectedUser = user;
        ErrorMessage = string.Empty;

        // If user has no password, attempt immediate login
        if (!user.HasPassword)
        {
            await LoginAsync(user.Name, string.Empty);
        }
    }

    public async Task LoginAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            ErrorMessage = "Username is required.";
            return;
        }

        ErrorMessage = string.Empty;
        IsConnecting = true;

        try
        {
            var cleanUrl = EmbyClient.NormalizeServerUrl(ServerUrl);
            var res = await _emby.AuthenticateAsync(cleanUrl, username, password);

            if (res != null && res.User != null)
            {
                // Save or update server in settings
                var existing = _settings.Current.Servers.FirstOrDefault(s => s.Url == cleanUrl && s.UserId == res.User.Id);
                if (existing != null)
                {
                    existing.AccessToken = res.AccessToken;
                    existing.Username = username;
                    existing.Name = ServerName;
                    existing.Version = ServerVersion;
                    existing.LastConnected = DateTime.UtcNow;
                    _settings.Current.ActiveServerId = existing.Id;
                }
                else
                {
                    var newServer = new ServerConfig
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Name = ServerName,
                        Url = cleanUrl,
                        UserId = res.User.Id,
                        Username = username,
                        AccessToken = res.AccessToken,
                        Version = ServerVersion,
                        AddedAt = DateTime.UtcNow,
                        LastConnected = DateTime.UtcNow
                    };
                    _settings.Current.Servers.Add(newServer);
                    _settings.Current.ActiveServerId = newServer.Id;
                }

                _settings.Current.FirstLaunchComplete = true;
                _settings.Save();

                // Trigger sync in background
                _ = _sync.SyncAllAsync();

                Authenticated?.Invoke();
            }
            else
            {
                ErrorMessage = "Invalid username or password. Please verify your credentials.";
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Authentication failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task SwitchToSavedServerAsync(ServerConfig server)
    {
        ErrorMessage = string.Empty;
        IsConnecting = true;

        try
        {
            _emby.Configure(server.Url, server.AccessToken, server.UserId);
            var views = await _emby.GetViewsAsync();

            if (views.Count > 0)
            {
                _settings.Current.ActiveServerId = server.Id;
                server.LastConnected = DateTime.UtcNow;
                _settings.Save();
                _ = _sync.SyncAllAsync();
                Authenticated?.Invoke();
            }
            else
            {
                // Auth expired: prompt reconnect
                ServerUrl = server.Url;
                ErrorMessage = $"Session expired for {server.Name}. Please sign in again.";
                await ConnectToServerAsync();
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to switch to {server.Name}: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public void RemoveSavedServer(ServerConfig server)
    {
        _settings.Current.Servers.RemoveAll(s => s.Id == server.Id);
        if (_settings.Current.ActiveServerId == server.Id)
        {
            _settings.Current.ActiveServerId = _settings.Current.Servers.FirstOrDefault()?.Id;
        }
        _settings.Save();
        LoadSavedServers();
    }

    public void GoBackToServers()
    {
        IsUserSelectionStep = false;
        SelectedUser = null;
        Password = string.Empty;
        ErrorMessage = string.Empty;
        IsManualMode = false;
    }
}
