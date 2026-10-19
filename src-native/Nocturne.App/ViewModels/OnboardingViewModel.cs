using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;

namespace Nocturne.App.ViewModels;

public class OnboardingViewModel : BaseViewModel
{
    private readonly EmbyClient _emby;
    private readonly SettingsService _settings;
    private readonly SyncEngine _sync;
    private readonly EmbyConnectService _connect;

    private string _serverUrl = "http://";
    private bool _isConnecting;
    private string _errorMessage = string.Empty;
    private bool _isUserSelectionStep;
    private bool _isEmbyConnectStep;
    private bool _isConnectServersStep;
    private string _connectPin = string.Empty;
    private bool _isPollingPin;
    private string? _lastConnectUserId;
    private string? _lastConnectToken;
    private string? _specificTargetServerUrl;
    private CancellationTokenSource? _pinPollingCts;

    private string _serverName = string.Empty;
    private string _serverVersion = string.Empty;
    private PublicUser? _selectedUser;
    private string _password = string.Empty;
    private bool _isManualMode;
    private string _manualUsername = string.Empty;

    public event Action? Authenticated;

    public ObservableCollection<ServerConfig> SavedServers { get; } = new();
    public ObservableCollection<PublicUser> PublicUsers { get; } = new();
    public ObservableCollection<ConnectServer> ConnectServers { get; } = new();

    public OnboardingViewModel(EmbyClient emby, SettingsService settings, SyncEngine sync, EmbyConnectService connect)
    {
        _emby = emby;
        _settings = settings;
        _sync = sync;
        _connect = connect;

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
        set
        {
            if (SetProperty(ref _isUserSelectionStep, value))
            {
                OnPropertyChanged(nameof(IsServerConnectStep));
                OnPropertyChanged(nameof(IsStep2Visible));
            }
        }
    }

    public bool IsEmbyConnectStep
    {
        get => _isEmbyConnectStep;
        set
        {
            if (SetProperty(ref _isEmbyConnectStep, value))
            {
                OnPropertyChanged(nameof(IsServerConnectStep));
                OnPropertyChanged(nameof(IsStep2Visible));
            }
        }
    }

    public bool IsConnectServersStep
    {
        get => _isConnectServersStep;
        set
        {
            if (SetProperty(ref _isConnectServersStep, value))
            {
                OnPropertyChanged(nameof(IsServerConnectStep));
                OnPropertyChanged(nameof(IsStep2Visible));
            }
        }
    }

    public bool IsServerConnectStep => !IsUserSelectionStep && !IsEmbyConnectStep && !IsConnectServersStep;
    public bool IsStep2Visible => IsUserSelectionStep && !IsEmbyConnectStep && !IsConnectServersStep;

    public string ConnectPin
    {
        get => _connectPin;
        set => SetProperty(ref _connectPin, value);
    }

    public bool IsPollingPin
    {
        get => _isPollingPin;
        set => SetProperty(ref _isPollingPin, value);
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
            IsEmbyConnectStep = false;
            IsConnectServersStep = false;
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
                await CompleteAuthenticationAsync(cleanUrl, res.AccessToken, res.User.Id, ServerName);
            }
            else
            {
                if (string.IsNullOrEmpty(password))
                {
                    ErrorMessage = "Authentication failed. If this user account has no local password or is linked to Emby Connect, please use 'Sign In with Emby Connect' below.";
                }
                else
                {
                    ErrorMessage = "Invalid username or password. Please verify your credentials, or use 'Sign In with Emby Connect' below.";
                }
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

    public async Task StartEmbyConnectPinFlowAsync(string? specificServerUrl = null)
    {
        ErrorMessage = string.Empty;
        _specificTargetServerUrl = specificServerUrl;

        _pinPollingCts?.Cancel();
        _pinPollingCts = new CancellationTokenSource();

        IsConnecting = true;
        try
        {
            var pinRes = await _connect.CreatePinAsync();
            if (pinRes == null || string.IsNullOrEmpty(pinRes.Pin))
            {
                ErrorMessage = "Could not generate Emby Connect PIN code. Please ensure you are connected to the internet.";
                return;
            }

            ConnectPin = pinRes.Pin;
            IsPollingPin = true;
            IsEmbyConnectStep = true;
            IsUserSelectionStep = false;
            IsConnectServersStep = false;

            var pin = pinRes.Pin;
            var token = _pinPollingCts.Token;

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
                            await HandlePinConfirmedAsync(pin);
                        });
                        break;
                    }

                    if (status != null && status.IsExpired)
                    {
                        await Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            ErrorMessage = "The PIN has expired. Please try again.";
                            IsEmbyConnectStep = false;
                            IsPollingPin = false;
                        });
                        break;
                    }
                }
            }, token);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to start Emby Connect sign-in: {ex.Message}";
            IsEmbyConnectStep = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task HandlePinConfirmedAsync(string pin)
    {
        IsPollingPin = false;
        IsConnecting = true;
        try
        {
            var exchange = await _connect.ExchangePinAsync(pin);
            if (exchange == null || string.IsNullOrEmpty(exchange.AccessToken) || string.IsNullOrEmpty(exchange.UserId))
            {
                ErrorMessage = "Failed to finalize Emby Connect authorization.";
                IsEmbyConnectStep = false;
                return;
            }

            _lastConnectUserId = exchange.UserId;
            _lastConnectToken = exchange.AccessToken;

            var servers = await _connect.GetServersAsync(exchange.UserId, exchange.AccessToken);

            // If a specific server was targeted (from step 2)
            if (!string.IsNullOrEmpty(_specificTargetServerUrl))
            {
                var cleanTarget = EmbyClient.NormalizeServerUrl(_specificTargetServerUrl);
                var matched = servers.FirstOrDefault(s =>
                    (!string.IsNullOrEmpty(s.Url) && cleanTarget.Contains(s.Url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(s.LocalAddress) && cleanTarget.Contains(s.LocalAddress.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                    ?? servers.FirstOrDefault();

                if (matched != null && !string.IsNullOrEmpty(matched.AccessKey))
                {
                    var (tok, uid, _) = await _connect.ExchangeServerTokenAsync(cleanTarget, exchange.UserId, matched.AccessKey);
                    if (!string.IsNullOrEmpty(tok) && !string.IsNullOrEmpty(uid))
                    {
                        await CompleteAuthenticationAsync(cleanTarget, tok, uid, matched.Name ?? ServerName ?? "Emby Server");
                        return;
                    }
                }

                ErrorMessage = $"Could not link {ServerName} via Emby Connect. Verify that this server is bound to your Emby Connect account.";
                IsEmbyConnectStep = false;
                IsUserSelectionStep = true;
                return;
            }

            // General Emby Connect flow (from step 1)
            if (servers.Count == 0)
            {
                ErrorMessage = "No Emby servers found on this Emby Connect account.";
                IsEmbyConnectStep = false;
                return;
            }

            if (servers.Count == 1)
            {
                var single = servers[0];
                var targetAddr = !string.IsNullOrWhiteSpace(single.LocalAddress) ? single.LocalAddress : single.Url;
                if (!string.IsNullOrEmpty(targetAddr))
                {
                    var cleanAddr = EmbyClient.NormalizeServerUrl(targetAddr);
                    var (tok, uid, _) = await _connect.ExchangeServerTokenAsync(cleanAddr, exchange.UserId, single.AccessKey);
                    if (!string.IsNullOrEmpty(tok) && !string.IsNullOrEmpty(uid))
                    {
                        await CompleteAuthenticationAsync(cleanAddr, tok, uid, single.Name ?? "Emby Server");
                        return;
                    }
                }
            }

            ConnectServers.Clear();
            foreach (var s in servers)
            {
                ConnectServers.Add(s);
            }
            IsEmbyConnectStep = false;
            IsConnectServersStep = true;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Emby Connect error: {ex.Message}";
            IsEmbyConnectStep = false;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    public async Task ConnectToEmbyConnectServerAsync(ConnectServer server)
    {
        if (string.IsNullOrEmpty(_lastConnectUserId))
        {
            ErrorMessage = "Emby Connect session missing. Please authorize again.";
            IsConnectServersStep = false;
            return;
        }

        IsConnecting = true;
        ErrorMessage = string.Empty;

        try
        {
            var candidateAddresses = new List<string>();
            if (!string.IsNullOrWhiteSpace(server.LocalAddress)) candidateAddresses.Add(server.LocalAddress.Trim());
            if (!string.IsNullOrWhiteSpace(server.Url)) candidateAddresses.Add(server.Url.Trim());

            foreach (var addr in candidateAddresses)
            {
                var clean = EmbyClient.NormalizeServerUrl(addr);
                var (tok, uid, _) = await _connect.ExchangeServerTokenAsync(clean, _lastConnectUserId, server.AccessKey);
                if (!string.IsNullOrEmpty(tok) && !string.IsNullOrEmpty(uid))
                {
                    await CompleteAuthenticationAsync(clean, tok, uid, server.Name);
                    return;
                }
            }

            ErrorMessage = $"Could not connect to {server.Name}. Please verify network connectivity to the server.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection to {server.Name} failed: {ex.Message}";
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private async Task CompleteAuthenticationAsync(string serverUrl, string token, string userId, string serverName)
    {
        var cleanUrl = EmbyClient.NormalizeServerUrl(serverUrl);
        _emby.Configure(cleanUrl, token, userId);

        var userInfo = await _emby.GetUserAsync(userId);
        var username = userInfo?.Name ?? "Emby User";

        var serverInfo = await _emby.GetServerInfoAsync(cleanUrl);
        var sName = serverInfo?.ServerName ?? serverName;
        var sVersion = serverInfo?.Version ?? "4.x";

        var existing = _settings.Current.Servers.FirstOrDefault(s => s.Url == cleanUrl && s.UserId == userId);
        if (existing != null)
        {
            existing.AccessToken = token;
            existing.Username = username;
            existing.Name = sName;
            existing.Version = sVersion;
            existing.LastConnected = DateTime.UtcNow;
            _settings.Current.ActiveServerId = existing.Id;
        }
        else
        {
            var newServer = new ServerConfig
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Name = sName,
                Url = cleanUrl,
                UserId = userId,
                Username = username,
                AccessToken = token,
                Version = sVersion,
                AddedAt = DateTime.UtcNow,
                LastConnected = DateTime.UtcNow
            };
            _settings.Current.Servers.Add(newServer);
            _settings.Current.ActiveServerId = newServer.Id;
        }

        _settings.Current.FirstLaunchComplete = true;
        _settings.Save();

        _ = _sync.SyncAllAsync();
        Authenticated?.Invoke();
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
        catch (Exception ex)
        {
            NocturneLogger.Warn("OnboardingViewModel", $"Could not launch browser: {ex.Message}");
        }
    }

    public void CancelEmbyConnect()
    {
        _pinPollingCts?.Cancel();
        IsEmbyConnectStep = false;
        IsConnectServersStep = false;
        IsPollingPin = false;
        ConnectPin = string.Empty;
        ErrorMessage = string.Empty;

        if (!string.IsNullOrEmpty(_specificTargetServerUrl))
        {
            IsUserSelectionStep = true;
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
        _pinPollingCts?.Cancel();
        IsUserSelectionStep = false;
        IsEmbyConnectStep = false;
        IsConnectServersStep = false;
        SelectedUser = null;
        Password = string.Empty;
        ErrorMessage = string.Empty;
        IsManualMode = false;
    }
}
