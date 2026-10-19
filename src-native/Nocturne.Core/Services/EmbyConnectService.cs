using System.Net.Http.Headers;
using System.Net.Http.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class EmbyConnectService
{
    private const string ConnectBaseUrl = "https://connect.emby.media/service/";
    private const string AppHeader = "Nocturne/4.0.0";
    private readonly HttpClient _http;
    private readonly string _deviceId;

    public string DeviceId => _deviceId;

    public EmbyConnectService()
    {
        _deviceId = EmbyClient.GetDeviceId();
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Add("X-Application", AppHeader);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<PinCreationResult?> CreatePinAsync()
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "deviceId", _deviceId }
            });

            var resp = await _http.PostAsync($"{ConnectBaseUrl}pin", content);
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<PinCreationResult>();
                NocturneLogger.Info("EmbyConnect", $"PIN generated successfully: {result?.Pin}");
                return result;
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                NocturneLogger.Warn("EmbyConnect", $"CreatePin failed ({resp.StatusCode}): {body}");
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyConnect", "CreatePin failed with exception", ex);
        }
        return null;
    }

    public async Task<PinCreationResult?> CheckPinStatusAsync(string pin)
    {
        try
        {
            var url = $"{ConnectBaseUrl}pin?pin={Uri.EscapeDataString(pin)}&deviceId={Uri.EscapeDataString(_deviceId)}";
            var resp = await _http.GetAsync(url);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<PinCreationResult>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyConnect", $"CheckPinStatus exception: {ex.Message}");
        }
        return null;
    }

    public async Task<PinExchangeResult?> ExchangePinAsync(string pin)
    {
        try
        {
            var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                { "pin", pin },
                { "deviceId", _deviceId }
            });

            var resp = await _http.PostAsync($"{ConnectBaseUrl}pin/authenticate", content);
            if (resp.IsSuccessStatusCode)
            {
                var result = await resp.Content.ReadFromJsonAsync<PinExchangeResult>();
                NocturneLogger.Info("EmbyConnect", $"PIN exchanged successfully! UserId: {result?.UserId}");
                return result;
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                NocturneLogger.Warn("EmbyConnect", $"ExchangePin failed ({resp.StatusCode}): {body}");
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyConnect", "ExchangePin failed with exception", ex);
        }
        return null;
    }

    public async Task<List<ConnectServer>> GetServersAsync(string connectUserId, string connectAccessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{ConnectBaseUrl}servers?userId={Uri.EscapeDataString(connectUserId)}");
            req.Headers.TryAddWithoutValidation("X-Connect-UserToken", connectAccessToken);

            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                var servers = await resp.Content.ReadFromJsonAsync<List<ConnectServer>>();
                NocturneLogger.Info("EmbyConnect", $"Retrieved {servers?.Count ?? 0} servers from Emby Connect");
                return servers ?? new List<ConnectServer>();
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync();
                NocturneLogger.Warn("EmbyConnect", $"GetServers failed ({resp.StatusCode}): {body}");
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyConnect", "GetServers exception", ex);
        }
        return new List<ConnectServer>();
    }

    public async Task<(string? AccessToken, string? LocalUserId, string? ResolvedUrl)> ExchangeServerTokenAsync(
        string serverUrl,
        string connectUserId,
        string accessKey)
    {
        var cleanUrl = EmbyClient.NormalizeServerUrl(serverUrl);
        var authHeader = $"MediaBrowser Client=\"Nocturne\", Device=\"Windows\", DeviceId=\"{_deviceId}\", Version=\"4.0.0\"";

        var candidateUrls = new[]
        {
            $"{cleanUrl}/Connect/Exchange?format=json&ConnectUserId={Uri.EscapeDataString(connectUserId)}",
            $"{cleanUrl}/emby/Connect/Exchange?format=json&ConnectUserId={Uri.EscapeDataString(connectUserId)}"
        };

        foreach (var url in candidateUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("X-Emby-Token", accessKey);
                req.Headers.TryAddWithoutValidation("X-MediaBrowser-Token", accessKey);
                req.Headers.TryAddWithoutValidation("X-Emby-Authorization", authHeader);
                req.Headers.TryAddWithoutValidation("Authorization", authHeader);
                req.Headers.TryAddWithoutValidation("X-Emby-Client", "Nocturne");
                req.Headers.TryAddWithoutValidation("X-Emby-Device-Name", "Windows");
                req.Headers.TryAddWithoutValidation("X-Emby-Device-Id", _deviceId);
                req.Headers.TryAddWithoutValidation("X-Emby-Client-Version", "4.0.0");
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var resp = await _http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<ConnectExchangeResponse>();
                    var localUserId = data?.LocalUserId ?? data?.UserId;
                    if (!string.IsNullOrEmpty(data?.AccessToken) && !string.IsNullOrEmpty(localUserId))
                    {
                        var resolvedBase = url.Contains("/emby/") ? $"{cleanUrl}/emby" : cleanUrl;
                        NocturneLogger.Info("EmbyConnect", $"Token exchange successful at {url}! LocalUserId: {localUserId}");
                        return (data.AccessToken, localUserId, resolvedBase);
                    }
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    NocturneLogger.Warn("EmbyConnect", $"Exchange failed at {url} ({resp.StatusCode}): {body}");
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("EmbyConnect", $"Exchange exception at {url}: {ex.Message}");
            }
        }

        return (null, null, null);
    }
}
