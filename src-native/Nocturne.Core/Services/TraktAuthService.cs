using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class TraktAuthService : ITraktAuthService
{
    private const string TraktApiBase = "https://api.trakt.tv";
    // TODO(client-id): the production Trakt client ID goes here. Empty until it is provided: until then only a
    // custom client ID (Settings -> Trakt -> Client ID, SetCustomClientId) can connect, and RequestDeviceCodeAsync
    // says so.
    private const string BundledClientId = "";

    private readonly HttpClient _http;
    private readonly DatabaseService _db;
    private TraktAuthState _state;
    private TraktConnectionState _connectionState = TraktConnectionState.Disconnected;

    public event EventHandler<TraktConnectionState>? StateChanged;

    public TraktConnectionState State => _connectionState;
    public string? Username => _state.Username;

    public TraktAuthService(DatabaseService db, HttpClient? httpClient = null)
    {
        _db = db;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(TraktApiBase), Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.DefaultRequestHeaders.Add("trakt-api-version", "2");

        _state = LoadState();
        EvaluateConnectionState();
    }

    public string GetActiveClientId()
    {
        if (_state.Mode == CredentialMode.Custom && !string.IsNullOrWhiteSpace(_state.CustomClientId))
        {
            return _state.CustomClientId.Trim();
        }
        return BundledClientId;
    }

    public string? GetAccessToken()
    {
        if (_state.AccessTokenDpapi == null || _state.AccessTokenDpapi.Length == 0)
            return null;

        try
        {
            return CredentialProtection.UnprotectString(_state.AccessTokenDpapi);
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("TraktAuthService", "Failed to decrypt access token", ex);
            return null;
        }
    }

    public string? CustomClientId => _state.CustomClientId;

    public void SetCustomClientId(string? clientId)
    {
        var trimmed = clientId?.Trim();
        if (string.Equals(_state.CustomClientId, trimmed, StringComparison.Ordinal))
            return;

        _state.CustomClientId = trimmed;
        _state.Mode = !string.IsNullOrEmpty(trimmed) ? CredentialMode.Custom : CredentialMode.Bundled;

        // Invalidation guard (§5 Rule 2): ClientId changed, invalidate minted tokens
        ClearTokens();
        SaveState();
        EvaluateConnectionState();
    }

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct)
    {
        var clientId = GetActiveClientId();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Trakt Client ID is not configured. Please enter a Client ID in Settings -> Trakt -> Advanced.");
        }

        var verifier = PkceHelper.GenerateCodeVerifier();
        var challenge = PkceHelper.GenerateCodeChallenge(verifier);

        var payload = new
        {
            client_id = clientId,
            code_challenge = challenge,
            code_challenge_method = "S256"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/device/code")
        {
            Content = JsonContent.Create(payload)
        };

        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            NocturneLogger.Error("TraktAuthService", $"Device code request failed with status {(int)response.StatusCode}");
            throw new HttpRequestException($"Failed to request device code from Trakt: {response.StatusCode} - {err}");
        }

        var json = await response.Content.ReadFromJsonAsync<TraktDeviceCodeApiDto>(cancellationToken: ct);
        if (json == null) throw new InvalidOperationException("Invalid device code response from Trakt.");

        NocturneLogger.Info("TraktAuthService", $"Requested device code successfully. User code: {json.UserCode}");

        return new DeviceCodeResponse(
            json.DeviceCode,
            json.UserCode,
            json.VerificationUrl,
            json.ExpiresIn,
            json.Interval,
            verifier
        );
    }

    public async Task<TraktAuthResult> PollForTokenAsync(DeviceCodeResponse codeResp, CancellationToken ct)
    {
        var clientId = GetActiveClientId();
        var pollRes = await ExecuteTokenPollAsync(codeResp.DeviceCode, codeResp.CodeVerifier, clientId, ct);
        if (pollRes.Status == TraktAuthStatus.Authorized)
        {
            await SaveTokensAndFetchUserAsync(pollRes, clientId, ct);
        }
        return pollRes;
    }

    private async Task<TraktAuthResult> ExecuteTokenPollAsync(string deviceCode, string verifier, string clientId, CancellationToken ct)
    {
        var payload = new
        {
            code = deviceCode,
            client_id = clientId,
            code_verifier = verifier
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/device/token")
        {
            Content = JsonContent.Create(payload)
        };

        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.OK) // 200
        {
            var tokenDto = await response.Content.ReadFromJsonAsync<TraktTokenApiDto>(cancellationToken: ct);
            if (tokenDto != null)
            {
                return new TraktAuthResult(
                    TraktAuthStatus.Authorized,
                    tokenDto.AccessToken,
                    tokenDto.RefreshToken,
                    tokenDto.ExpiresIn
                );
            }
        }
        else if (response.StatusCode == HttpStatusCode.BadRequest) // 400 Pending
        {
            return new TraktAuthResult(TraktAuthStatus.Pending);
        }
        else if (response.StatusCode == (HttpStatusCode)429) // 429 SlowDown
        {
            return new TraktAuthResult(TraktAuthStatus.SlowDown);
        }
        else if (response.StatusCode == HttpStatusCode.NotFound) // 404
        {
            return new TraktAuthResult(TraktAuthStatus.NotFound);
        }
        else if (response.StatusCode == HttpStatusCode.Conflict) // 409 AlreadyUsed
        {
            return new TraktAuthResult(TraktAuthStatus.AlreadyUsed);
        }
        else if (response.StatusCode == HttpStatusCode.Gone) // 410 Expired
        {
            return new TraktAuthResult(TraktAuthStatus.Expired);
        }
        else if ((int)response.StatusCode == 418) // 418 Denied
        {
            return new TraktAuthResult(TraktAuthStatus.Denied);
        }

        return new TraktAuthResult(TraktAuthStatus.Error, ErrorMessage: $"Unexpected HTTP status: {(int)response.StatusCode}");
    }

    private async Task SaveTokensAndFetchUserAsync(TraktAuthResult auth, string clientId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(auth.AccessToken)) return;

        _state.AccessTokenDpapi = CredentialProtection.ProtectString(auth.AccessToken);
        _state.RefreshTokenDpapi = !string.IsNullOrEmpty(auth.RefreshToken)
            ? CredentialProtection.ProtectString(auth.RefreshToken)
            : null;
        _state.ExpiresUtc = DateTime.UtcNow.AddSeconds(auth.ExpiresIn > 0 ? auth.ExpiresIn : 7776000); // 90 days default
        _state.ClientIdUsedForToken = clientId;

        // Fetch Trakt username (/users/me)
        try
        {
            using var userReq = new HttpRequestMessage(HttpMethod.Get, "/users/me");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
            userReq.Headers.Add("trakt-api-key", clientId);

            using var userResp = await _http.SendAsync(userReq, ct);
            if (userResp.IsSuccessStatusCode)
            {
                var userDto = await userResp.Content.ReadFromJsonAsync<TraktUserMeDto>(cancellationToken: ct);
                if (userDto?.User != null)
                {
                    _state.Username = userDto.User.Username;
                }
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("TraktAuthService", $"Failed to fetch Trakt profile name: {ex.Message}");
        }

        SaveState();
        SetConnectionState(TraktConnectionState.Connected);
        NocturneLogger.Info("TraktAuthService", $"Successfully authorized Trakt for user: {_state.Username ?? "Unknown"}");
    }

    public async Task<bool> RefreshIfNeededAsync(CancellationToken ct)
    {
        if (_state.RefreshTokenDpapi == null || _state.RefreshTokenDpapi.Length == 0)
            return false;

        // Invalidation check
        var clientId = GetActiveClientId();
        if (!string.Equals(_state.ClientIdUsedForToken, clientId, StringComparison.Ordinal))
        {
            ClearTokens();
            SaveState();
            SetConnectionState(TraktConnectionState.Disconnected);
            return false;
        }

        // Proactive refresh when < 24h before expiry (§6.3)
        if (DateTime.UtcNow.AddHours(24) < _state.ExpiresUtc)
        {
            return true; // Token still comfortably valid
        }

        string? refreshToken;
        try
        {
            refreshToken = CredentialProtection.UnprotectString(_state.RefreshTokenDpapi);
        }
        catch
        {
            ClearTokens();
            SaveState();
            SetConnectionState(TraktConnectionState.NeedsReconnection);
            return false;
        }

        if (string.IsNullOrEmpty(refreshToken))
        {
            ClearTokens();
            SaveState();
            SetConnectionState(TraktConnectionState.NeedsReconnection);
            return false;
        }

        NocturneLogger.Info("TraktAuthService", "Proactively refreshing Trakt access token...");

        var payload = new
        {
            refresh_token = refreshToken,
            client_id = clientId,
            grant_type = "refresh_token"
        };

        HttpStatusCode status;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
            {
                Content = JsonContent.Create(payload)
            };

            using var response = await _http.SendAsync(request, ct);
            status = response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var tokenDto = await response.Content.ReadFromJsonAsync<TraktTokenApiDto>(cancellationToken: ct);
                if (tokenDto != null && !string.IsNullOrEmpty(tokenDto.AccessToken))
                {
                    _state.AccessTokenDpapi = CredentialProtection.ProtectString(tokenDto.AccessToken);
                    if (!string.IsNullOrEmpty(tokenDto.RefreshToken))
                    {
                        _state.RefreshTokenDpapi = CredentialProtection.ProtectString(tokenDto.RefreshToken);
                    }
                    _state.ExpiresUtc = DateTime.UtcNow.AddSeconds(tokenDto.ExpiresIn > 0 ? tokenDto.ExpiresIn : 7776000);
                    _state.ClientIdUsedForToken = clientId;
                    SaveState();
                    SetConnectionState(TraktConnectionState.Connected);
                    NocturneLogger.Info("TraktAuthService", "Trakt token refreshed successfully.");
                    return true;
                }
            }

            NocturneLogger.Warn("TraktAuthService", $"Token refresh returned status {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            // Network down, DNS, timeout, a malformed body: none of it says the tokens are bad. Keep them.
            NocturneLogger.Warn("TraktAuthService", $"Token refresh failed transiently ({ex.Message}); keeping the current tokens.");
            return false;
        }

        // Only Trakt rejecting the refresh token (400 invalid_grant / 401) ends the session (§6.3); a 5xx or 429 is
        // transient. Clearing on any failure signed the user out on every network blip.
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            ClearTokens();
            SaveState();
            SetConnectionState(TraktConnectionState.NeedsReconnection);
        }
        return false;
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        var token = GetAccessToken();
        var clientId = GetActiveClientId();

        if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(clientId))
        {
            try
            {
                var payload = new { token = token, client_id = clientId };
                using var req = new HttpRequestMessage(HttpMethod.Post, "/oauth/revoke")
                {
                    Content = JsonContent.Create(payload)
                };
                await _http.SendAsync(req, ct);
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("TraktAuthService", $"Revoke call warning: {ex.Message}");
            }
        }

        ClearTokens();
        _state.Username = null;
        SaveState();
        SetConnectionState(TraktConnectionState.Disconnected);
        NocturneLogger.Info("TraktAuthService", "Trakt disconnected and credentials cleared.");
    }

    private void ClearTokens()
    {
        _state.AccessTokenDpapi = null;
        _state.RefreshTokenDpapi = null;
        _state.ExpiresUtc = DateTime.MinValue;
        _state.ClientIdUsedForToken = null;
    }

    private void EvaluateConnectionState()
    {
        var activeClientId = GetActiveClientId();

        // §5 Rule 2: ClientIdUsedForToken guard
        if (_state.AccessTokenDpapi != null && _state.AccessTokenDpapi.Length > 0)
        {
            if (!string.Equals(_state.ClientIdUsedForToken, activeClientId, StringComparison.Ordinal))
            {
                NocturneLogger.Warn("TraktAuthService", "Active Client ID differs from Client ID that minted token; resetting state.");
                ClearTokens();
                SaveState();
                SetConnectionState(TraktConnectionState.Disconnected);
                return;
            }

            if (DateTime.UtcNow > _state.ExpiresUtc)
            {
                SetConnectionState(TraktConnectionState.NeedsReconnection);
                return;
            }

            SetConnectionState(TraktConnectionState.Connected);
        }
        else
        {
            SetConnectionState(TraktConnectionState.Disconnected);
        }
    }

    private void SetConnectionState(TraktConnectionState newState)
    {
        if (_connectionState != newState)
        {
            _connectionState = newState;
            StateChanged?.Invoke(this, newState);
        }
    }

    private TraktAuthState LoadState()
    {
        var modeStr = _db.GetIntegrationState("trakt", "mode");
        var customId = _db.GetIntegrationState("trakt", "custom_client_id");
        var accessHex = _db.GetIntegrationState("trakt", "access_token_dpapi");
        var refreshHex = _db.GetIntegrationState("trakt", "refresh_token_dpapi");
        var expStr = _db.GetIntegrationState("trakt", "expires_utc");
        var usedId = _db.GetIntegrationState("trakt", "client_id_used");
        var user = _db.GetIntegrationState("trakt", "username");

        var state = new TraktAuthState
        {
            Mode = Enum.TryParse<CredentialMode>(modeStr, out var m) ? m : CredentialMode.Bundled,
            CustomClientId = customId,
            ClientIdUsedForToken = usedId,
            Username = user,
            AccessTokenDpapi = !string.IsNullOrEmpty(accessHex) ? Convert.FromHexString(accessHex) : null,
            RefreshTokenDpapi = !string.IsNullOrEmpty(refreshHex) ? Convert.FromHexString(refreshHex) : null,
            ExpiresUtc = long.TryParse(expStr, out var expUnix) && expUnix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime
                : DateTime.MinValue
        };

        return state;
    }

    private void SaveState()
    {
        _db.SetIntegrationState("trakt", "mode", _state.Mode.ToString());
        if (!string.IsNullOrEmpty(_state.CustomClientId))
            _db.SetIntegrationState("trakt", "custom_client_id", _state.CustomClientId);
        else
            _db.SetIntegrationState("trakt", "custom_client_id", "");

        _db.SetIntegrationState("trakt", "access_token_dpapi", _state.AccessTokenDpapi != null ? Convert.ToHexString(_state.AccessTokenDpapi) : "");
        _db.SetIntegrationState("trakt", "refresh_token_dpapi", _state.RefreshTokenDpapi != null ? Convert.ToHexString(_state.RefreshTokenDpapi) : "");
        // DateTime.MinValue (no token) is Kind=Unspecified: casting it to DateTimeOffset applies the local offset and
        // throws on UTC+ machines. "0" means no expiry.
        _db.SetIntegrationState("trakt", "expires_utc", _state.ExpiresUtc == DateTime.MinValue
            ? "0"
            : new DateTimeOffset(DateTime.SpecifyKind(_state.ExpiresUtc, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString());
        _db.SetIntegrationState("trakt", "client_id_used", _state.ClientIdUsedForToken ?? "");
        _db.SetIntegrationState("trakt", "username", _state.Username ?? "");
    }

    private class TraktDeviceCodeApiDto
    {
        [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = string.Empty;
        [JsonPropertyName("user_code")] public string UserCode { get; set; } = string.Empty;
        [JsonPropertyName("verification_url")] public string VerificationUrl { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("interval")] public int Interval { get; set; }
    }

    private class TraktTokenApiDto
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = string.Empty;
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
    }

    private class TraktUserMeDto
    {
        [JsonPropertyName("user")] public TraktUserProfileDto? User { get; set; }
    }

    private class TraktUserProfileDto
    {
        [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;
    }
}
