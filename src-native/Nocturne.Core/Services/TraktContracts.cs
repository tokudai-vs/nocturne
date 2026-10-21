using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Nocturne.Core.Services;

public enum CredentialMode
{
    Bundled,
    Custom
}

public enum TraktConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    NeedsReconnection
}

public enum TraktAuthStatus
{
    Authorized,      // 200
    Pending,         // 400
    SlowDown,        // 429
    NotFound,        // 404
    AlreadyUsed,     // 409
    Expired,         // 410
    Denied,          // 418
    Error
}

public record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUrl,
    int ExpiresIn,
    int Interval,
    string CodeVerifier);

public record TraktAuthResult(
    TraktAuthStatus Status,
    string? AccessToken = null,
    string? RefreshToken = null,
    int ExpiresIn = 0,
    string? Username = null,
    string? ErrorMessage = null);

public record TraktQueueItem(
    long QueueId,
    long GroupId,
    string Action,
    double Progress,
    string? Payload,
    long QueuedUtc,
    int Attempts);

public class TraktAuthState
{
    public CredentialMode Mode { get; set; } = CredentialMode.Bundled;
    public string? CustomClientId { get; set; }
    public byte[]? AccessTokenDpapi { get; set; }
    public byte[]? RefreshTokenDpapi { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public string? ClientIdUsedForToken { get; set; }
    public string? Username { get; set; }
}

public class TmdbAuthState
{
    public CredentialMode Mode { get; set; } = CredentialMode.Bundled;
    public string? CustomApiKey { get; set; }
}

public class WatchlistCard
{
    public long GroupId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string? ImageTag { get; set; }
    public string? ImageServerId { get; set; }
    public string? ImageItemId { get; set; }
    public string? TmdbKey { get; set; }
    public string? TmdbPosterPath { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Ref { get; set; } = string.Empty;
    public string ItemType { get; set; } = "Movie";
    public long AddedUtc { get; set; }
    public bool IsInLibrary { get; set; }
    public string? PendingOp { get; set; }
}

public class TmdbMetadata
{
    public string ProviderKey { get; set; } = string.Empty;
    public string? PosterPath { get; set; }
    public string? BackdropPath { get; set; }
    public string? StillPath { get; set; }
    public string? Overview { get; set; }
    public double? VoteAverage { get; set; }
    public long FetchedUtc { get; set; }
    public bool NotFound { get; set; }
}

public record EnrichProgress(int Total, int Processed, int Enriched, int NotFound, string CurrentTitle);

public interface ITraktAuthService
{
    Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken ct);
    Task<TraktAuthResult>    PollForTokenAsync(DeviceCodeResponse c, CancellationToken ct);
    Task<bool>               RefreshIfNeededAsync(CancellationToken ct);
    Task                     DisconnectAsync(CancellationToken ct);
    TraktConnectionState     State { get; }
    string?                  Username { get; }
    string                   GetActiveClientId();
    string?                  GetAccessToken();
    event EventHandler<TraktConnectionState>? StateChanged;
}

public interface ITraktScrobbleService
{
    void Start(long groupId, double progress);
    void Pause(long groupId, double progress);
    void Stop (long groupId, double progress);
    int SkippedProvisionalCount { get; }
}

public interface ITraktSyncService
{
    Task PullWatchedAsync(CancellationToken ct);
    Task PullWatchlistAsync(CancellationToken ct);
    Task FullReconcileAsync(IProgress<int> p, CancellationToken ct);
    DateTime? LastPullTime { get; }
}

public interface IWatchlistService
{
    Task<IReadOnlyList<WatchlistCard>> GetAsync(CancellationToken ct);
    Task AddAsync(long groupId, CancellationToken ct);
    Task RemoveAsync(long groupId, CancellationToken ct);
    Task AddUnownedAsync(string tmdbKey, string title, int? year, string itemType, CancellationToken ct);
}

public interface ITmdbEnrichmentService
{
    Task RunAsync(IProgress<EnrichProgress> p, CancellationToken ct);
    Task<TmdbMetadata?> GetAsync(string providerKey, CancellationToken ct);
    string GetActiveApiKey();
    void SetCustomApiKey(string? apiKey);
    int EnrichedCount { get; }
    int PendingCount { get; }
    int NotFoundCount { get; }
}

public interface IQueueDrainWorker
{
    Task DrainAsync(CancellationToken ct);
    int PendingQueueDepth { get; }
    int FailedAttemptsCount { get; }
}
