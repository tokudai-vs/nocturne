using System.IO;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Web;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class AuthenticatePayload
{
    [JsonPropertyName("Username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("Pw")]
    public string Pw { get; set; } = string.Empty;
}

public class PlaybackProgressPayload
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("MediaSourceId")]
    public string? MediaSourceId { get; set; }

    [JsonPropertyName("PositionTicks")]
    public long PositionTicks { get; set; }

    [JsonPropertyName("IsPaused")]
    public bool IsPaused { get; set; }

    [JsonPropertyName("EventName")]
    public string EventName { get; set; } = "TimeUpdate";
}

public class PlaybackStartPayload
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("MediaSourceId")]
    public string? MediaSourceId { get; set; }

    [JsonPropertyName("PositionTicks")]
    public long PositionTicks { get; set; }
}

public class PlaybackStopPayload
{
    [JsonPropertyName("ItemId")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("MediaSourceId")]
    public string? MediaSourceId { get; set; }

    [JsonPropertyName("PositionTicks")]
    public long PositionTicks { get; set; }
}

public class EmbyClient
{
    private readonly HttpClient _http;
    private string _baseUrl = string.Empty;
    private string _userId = string.Empty;
    private string _token = string.Empty;
    private readonly string _deviceId;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public const string FullItemFields = "Overview,RunTimeTicks,CommunityRating,IndexNumber,ParentIndexNumber,Genres,Studios,MediaSources,Taglines,ImageTags,BackdropImageTags,RemoteTrailers,People,ProviderIds,OfficialRating";
    public const string LightItemFields = "Overview,RunTimeTicks,CommunityRating,IndexNumber,ParentIndexNumber,ImageTags,BackdropImageTags,ProviderIds";

    public EmbyClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _deviceId = GetOrCreateDeviceId();
    }

    private static string GetOrCreateDeviceId()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "Nocturne");
            Directory.CreateDirectory(dir);
            var devPath = Path.Combine(dir, "device.json");
            if (File.Exists(devPath))
            {
                var content = File.ReadAllText(devPath);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("deviceId", out var prop))
                {
                    var val = prop.GetString();
                    if (!string.IsNullOrEmpty(val)) return val;
                }
            }
            var newId = Guid.NewGuid().ToString("N");
            File.WriteAllText(devPath, JsonSerializer.Serialize(new { deviceId = newId }));
            return newId;
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"Could not save device.json: {ex.Message}");
            return Guid.NewGuid().ToString("N");
        }
    }

    public static string GetDeviceId() => GetOrCreateDeviceId();

    public static string NormalizeServerUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim().TrimEnd('/');
        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = "http://" + trimmed;
        }
        return trimmed;
    }

    public string BaseUrl => _baseUrl;
    public string UserId => _userId;
    public string Token => _token;
    public bool IsAuthenticated => !string.IsNullOrEmpty(_token);

    public void Configure(string url, string token = "", string userId = "")
    {
        _baseUrl = NormalizeServerUrl(url);
        _token = token;
        _userId = userId;

        _http.DefaultRequestHeaders.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var authHeader = $"MediaBrowser Client=\"Nocturne\", Device=\"Windows\", DeviceId=\"{_deviceId}\", Version=\"4.0.0\"";
        if (!string.IsNullOrEmpty(_token))
        {
            authHeader += $", Token=\"{_token}\"";
        }

        // Use TryAddWithoutValidation to bypass RFC strict parser
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Authorization", authHeader);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", authHeader);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Client", "Nocturne");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Device-Name", "Windows");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Device-Id", _deviceId);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Client-Version", "4.0.0");

        if (!string.IsNullOrEmpty(_token))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Emby-Token", _token);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("X-MediaBrowser-Token", _token);
        }
    }

    private string BuildUrl(string path)
    {
        var p = path.StartsWith("/") ? path : "/" + path;
        if (_baseUrl.EndsWith("/emby", StringComparison.OrdinalIgnoreCase))
        {
            return $"{_baseUrl}{p}";
        }
        return $"{_baseUrl}/emby{p}";
    }

    public async Task<EmbyServerInfo?> GetServerInfoAsync(string url)
    {
        var cleanUrl = NormalizeServerUrl(url);
        Configure(cleanUrl);

        // Try /emby/System/Info/Public first
        try
        {
            var resp = await _http.GetAsync(BuildUrl("/System/Info/Public"));
            if (resp.IsSuccessStatusCode)
            {
                var info = await resp.Content.ReadFromJsonAsync<EmbyServerInfo>();
                NocturneLogger.Info("EmbyClient", $"Connected to {info?.ServerName} (v{info?.Version}) via /emby");
                return info;
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"/emby/System/Info/Public check failed: {ex.Message}");
        }

        // Fallback without /emby prefix
        try
        {
            var resp = await _http.GetAsync($"{cleanUrl}/System/Info/Public");
            if (resp.IsSuccessStatusCode)
            {
                var info = await resp.Content.ReadFromJsonAsync<EmbyServerInfo>();
                NocturneLogger.Info("EmbyClient", $"Connected to {info?.ServerName} (v{info?.Version}) without /emby");
                return info;
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"{cleanUrl}/System/Info/Public check failed: {ex.Message}");
        }

        return null;
    }

    public async Task<List<PublicUser>> GetPublicUsersAsync(string url)
    {
        var cleanUrl = NormalizeServerUrl(url);
        Configure(cleanUrl);

        try
        {
            var resp = await _http.GetAsync(BuildUrl("/Users/Public"));
            if (resp.IsSuccessStatusCode)
            {
                var users = await resp.Content.ReadFromJsonAsync<List<PublicUser>>();
                return users ?? new List<PublicUser>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"GetPublicUsers (/emby) error: {ex.Message}");
        }

        try
        {
            var resp = await _http.GetAsync($"{cleanUrl}/Users/Public");
            if (resp.IsSuccessStatusCode)
            {
                var users = await resp.Content.ReadFromJsonAsync<List<PublicUser>>();
                return users ?? new List<PublicUser>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"GetPublicUsers (root) error: {ex.Message}");
        }

        return new List<PublicUser>();
    }

    public async Task<AuthResult?> AuthenticateAsync(string url, string username, string password)
    {
        var normUrl = NormalizeServerUrl(url);
        Configure(normUrl);

        var hasPassword = !string.IsNullOrEmpty(password);
        NocturneLogger.Info("EmbyClient", $"Attempting auth for user '{username}' at {normUrl} (hasPassword: {hasPassword})...");

        var endpoints = new[]
        {
            BuildUrl("/Users/AuthenticateByName"),
            $"{normUrl}/Users/AuthenticateByName"
        };

        foreach (var targetUrl in endpoints)
        {
            // Strategy 1: Standard JSON with Username & Pw
            try
            {
                var payload = new AuthenticatePayload
                {
                    Username = username,
                    Pw = password ?? string.Empty
                };
                var json = JsonSerializer.Serialize(payload, JsonOptions);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(targetUrl, content);
                if (resp.IsSuccessStatusCode)
                {
                    var res = await resp.Content.ReadFromJsonAsync<AuthResult>();
                    if (res != null && res.User != null)
                    {
                        Configure(normUrl, res.AccessToken, res.User.Id);
                        NocturneLogger.Info("EmbyClient", $"Authentication successful for {username} (JSON Pw)!");
                        return res;
                    }
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    NocturneLogger.Warn("EmbyClient", $"JSON auth returned {resp.StatusCode} at {targetUrl}: {body}");
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("EmbyClient", $"JSON auth error at {targetUrl}: {ex.Message}");
            }

            // Strategy 2: Form URL Encoded: Username=...&Pw=...
            try
            {
                var formData = new Dictionary<string, string>
                {
                    { "Username", username },
                    { "Pw", password ?? string.Empty }
                };
                using var formContent = new FormUrlEncodedContent(formData);
                var resp = await _http.PostAsync(targetUrl, formContent);
                if (resp.IsSuccessStatusCode)
                {
                    var res = await resp.Content.ReadFromJsonAsync<AuthResult>();
                    if (res != null && res.User != null)
                    {
                        Configure(normUrl, res.AccessToken, res.User.Id);
                        NocturneLogger.Info("EmbyClient", $"Authentication successful for {username} (Form Pw)!");
                        return res;
                    }
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("EmbyClient", $"Form auth error at {targetUrl}: {ex.Message}");
            }

            // Strategy 3 (when password is empty or not set): Omit Pw property entirely
            if (!hasPassword)
            {
                try
                {
                    var noPwJson = JsonSerializer.Serialize(new { Username = username }, JsonOptions);
                    using var content = new StringContent(noPwJson, Encoding.UTF8, "application/json");
                    var resp = await _http.PostAsync(targetUrl, content);
                    if (resp.IsSuccessStatusCode)
                    {
                        var res = await resp.Content.ReadFromJsonAsync<AuthResult>();
                        if (res != null && res.User != null)
                        {
                            Configure(normUrl, res.AccessToken, res.User.Id);
                            NocturneLogger.Info("EmbyClient", $"Authentication successful for {username} (JSON no-pw)!");
                            return res;
                        }
                    }
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("EmbyClient", $"JSON no-pw auth error at {targetUrl}: {ex.Message}");
                }

                // Strategy 4 (when password is empty): Form urlencoded without Pw
                try
                {
                    var formData = new Dictionary<string, string> { { "Username", username } };
                    using var formContent = new FormUrlEncodedContent(formData);
                    var resp = await _http.PostAsync(targetUrl, formContent);
                    if (resp.IsSuccessStatusCode)
                    {
                        var res = await resp.Content.ReadFromJsonAsync<AuthResult>();
                        if (res != null && res.User != null)
                        {
                            Configure(normUrl, res.AccessToken, res.User.Id);
                            NocturneLogger.Info("EmbyClient", $"Authentication successful for {username} (Form no-pw)!");
                            return res;
                        }
                    }
                }
                catch (Exception ex)
                {
                    NocturneLogger.Warn("EmbyClient", $"Form no-pw auth error at {targetUrl}: {ex.Message}");
                }
            }
        }

        NocturneLogger.Warn("EmbyClient", $"All direct authentication strategies failed for '{username}' at {normUrl}. Account may be linked to Emby Connect without local password.");
        return null;
    }

    public async Task<EmbyUser?> GetUserAsync(string userId)
    {
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{userId}"));
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<EmbyUser>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"GetUserAsync failed: {ex.Message}");
        }
        return null;
    }

    public async Task<List<EmbyItem>> GetViewsAsync()
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{_userId}/Views"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", "GetViews failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<ItemsResult<EmbyItem>> GetItemsAsync(
        string? parentId = null,
        string? includeItemTypes = null,
        int limit = 50,
        int startIndex = 0,
        string sortBy = "SortName",
        string sortOrder = "Ascending")
    {
        if (!IsAuthenticated) return new ItemsResult<EmbyItem>();

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["Recursive"] = "true";
        query["Limit"] = limit.ToString();
        query["StartIndex"] = startIndex.ToString();
        query["SortBy"] = sortBy;
        query["SortOrder"] = sortOrder;
        query["Fields"] = FullItemFields;

        if (!string.IsNullOrEmpty(parentId)) query["ParentId"] = parentId;
        if (!string.IsNullOrEmpty(includeItemTypes)) query["IncludeItemTypes"] = includeItemTypes;

        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{_userId}/Items?{query}"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res ?? new ItemsResult<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", "GetItems failed", ex);
        }
        return new ItemsResult<EmbyItem>();
    }

    public async Task<EmbyItem?> GetItemAsync(string itemId)
    {
        if (!IsAuthenticated) return null;
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{_userId}/Items/{itemId}?Fields={FullItemFields}"));
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"GetItem ({itemId}) failed", ex);
        }
        return null;
    }

    public async Task<List<EmbyItem>> GetResumeAsync(int limit = 16)
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        var query = $"Recursive=true&SortBy=DatePlayed&SortOrder=Descending&Filters=IsResumable&Limit={limit}&Fields={LightItemFields}";
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{_userId}/Items?{query}"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", "GetResume failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<List<EmbyItem>> GetNextUpAsync(int limit = 16)
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Shows/NextUp?UserId={_userId}&Limit={limit}&Fields={LightItemFields}"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", "GetNextUp failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<List<EmbyItem>> GetLatestAsync(string? parentId = null, int limit = 16)
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        var query = $"Limit={limit}&Fields={LightItemFields}";
        if (!string.IsNullOrEmpty(parentId)) query += $"&ParentId={parentId}";
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{_userId}/Items/Latest?{query}"));
            if (resp.IsSuccessStatusCode)
            {
                var items = await resp.Content.ReadFromJsonAsync<List<EmbyItem>>();
                return items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", "GetLatest failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<List<EmbyItem>> GetSeasonsAsync(string seriesId)
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Shows/{seriesId}/Seasons?UserId={_userId}&Fields=Overview,IndexNumber,ImageTags"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"GetSeasons for {seriesId} failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<List<EmbyItem>> GetEpisodesAsync(string seriesId, string seasonId)
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Shows/{seriesId}/Episodes?SeasonId={seasonId}&UserId={_userId}&Fields={FullItemFields}"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"GetEpisodes for {seasonId} failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<List<EmbyItem>> GetSimilarAsync(string itemId, int limit = 12)
    {
        if (!IsAuthenticated) return new List<EmbyItem>();
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Items/{itemId}/Similar?UserId={_userId}&Limit={limit}&Fields={LightItemFields}"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"GetSimilar for {itemId} failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<List<EmbyItem>> SearchAsync(string term, int limit = 30)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(term)) return new List<EmbyItem>();
        var query = $"SearchTerm={HttpUtility.UrlEncode(term)}&Recursive=true&Limit={limit}&IncludeItemTypes=Movie,Series,Episode&Fields={LightItemFields}";
        try
        {
            var resp = await _http.GetAsync(BuildUrl($"/Users/{_userId}/Items?{query}"));
            if (resp.IsSuccessStatusCode)
            {
                var res = await resp.Content.ReadFromJsonAsync<ItemsResult<EmbyItem>>();
                return res?.Items ?? new List<EmbyItem>();
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"Search for '{term}' failed", ex);
        }
        return new List<EmbyItem>();
    }

    public async Task<bool> MarkPlayedAsync(string itemId)
    {
        if (!IsAuthenticated) return false;
        try
        {
            var resp = await _http.PostAsync(BuildUrl($"/Users/{_userId}/PlayedItems/{itemId}"), null);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"MarkPlayed ({itemId}) failed", ex);
            return false;
        }
    }

    public async Task<bool> MarkUnplayedAsync(string itemId)
    {
        if (!IsAuthenticated) return false;
        try
        {
            var resp = await _http.DeleteAsync(BuildUrl($"/Users/{_userId}/PlayedItems/{itemId}"));
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"MarkUnplayed ({itemId}) failed", ex);
            return false;
        }
    }

    public async Task<bool> ToggleFavoriteAsync(string itemId, bool isFavorite)
    {
        if (!IsAuthenticated) return false;
        try
        {
            var resp = isFavorite
                ? await _http.PostAsync(BuildUrl($"/Users/{_userId}/FavoriteItems/{itemId}"), null)
                : await _http.DeleteAsync(BuildUrl($"/Users/{_userId}/FavoriteItems/{itemId}"));
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("EmbyClient", $"ToggleFavorite ({itemId}) failed", ex);
            return false;
        }
    }

    // Playback Session Reporting to Emby Server
    public async Task ReportPlaybackStartAsync(string itemId, string? mediaSourceId, long positionTicks = 0)
    {
        if (!IsAuthenticated) return;
        try
        {
            var payload = new PlaybackStartPayload
            {
                ItemId = itemId,
                MediaSourceId = mediaSourceId,
                PositionTicks = positionTicks
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync(BuildUrl("/Sessions/Playing"), content);
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"ReportPlaybackStart failed: {ex.Message}");
        }
    }

    public async Task ReportPlaybackProgressAsync(string itemId, string? mediaSourceId, long positionTicks, bool isPaused = false)
    {
        if (!IsAuthenticated) return;
        try
        {
            var payload = new PlaybackProgressPayload
            {
                ItemId = itemId,
                MediaSourceId = mediaSourceId,
                PositionTicks = positionTicks,
                IsPaused = isPaused,
                EventName = isPaused ? "Pause" : "TimeUpdate"
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync(BuildUrl("/Sessions/Playing/Progress"), content);
        }
        catch { }
    }

    public async Task ReportPlaybackStoppedAsync(string itemId, string? mediaSourceId, long positionTicks)
    {
        if (!IsAuthenticated) return;
        try
        {
            var payload = new PlaybackStopPayload
            {
                ItemId = itemId,
                MediaSourceId = mediaSourceId,
                PositionTicks = positionTicks
            };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            await _http.PostAsync(BuildUrl("/Sessions/Playing/Stopped"), content);
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("EmbyClient", $"ReportPlaybackStopped failed: {ex.Message}");
        }
    }

    public string BuildImageUrl(string itemId, string imageType = "Primary", int? maxWidth = null, string? tag = null)
    {
        if (string.IsNullOrEmpty(_baseUrl)) return string.Empty;
        var url = BuildUrl($"/Items/{itemId}/Images/{imageType}");
        var query = new List<string>();
        if (maxWidth.HasValue) query.Add($"MaxWidth={maxWidth.Value}");
        if (!string.IsNullOrEmpty(tag)) query.Add($"Tag={tag}");
        if (!string.IsNullOrEmpty(_token)) query.Add($"api_key={_token}");

        return query.Count > 0 ? $"{url}?{string.Join("&", query)}" : url;
    }

    public string GetStreamUrl(string itemId, string mediaSourceId)
    {
        return $"{BuildUrl($"/Videos/{itemId}/stream")}?static=true&MediaSourceId={mediaSourceId}&api_key={_token}";
    }
}
