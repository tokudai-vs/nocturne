using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

public class TraktDeviceCode
{
    [JsonPropertyName("device_code")]
    public string DeviceCode { get; set; } = string.Empty;

    [JsonPropertyName("user_code")]
    public string UserCode { get; set; } = string.Empty;

    [JsonPropertyName("verification_url")]
    public string VerificationUrl { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("interval")]
    public int Interval { get; set; }
}

public class TraktTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = string.Empty;

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }
}

public class TraktUserStats
{
    public int MoviesWatched { get; set; }
    public int EpisodesWatched { get; set; }
    public int TotalHours { get; set; }
    public int DistinctShows { get; set; }
}

public class TraktWatchlistItem
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Type { get; set; } = "movie";
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class AnalyticsStats
{
    public long TotalWatchTimeSeconds { get; set; }
    public int MoviesWatched { get; set; }
    public int EpisodesWatched { get; set; }
    public int DistinctSeriesCount { get; set; }
    public double AverageHoursPerWeek { get; set; }
    public TraktUserStats? TraktLifetime { get; set; }
    public List<GenreCount> TopGenres { get; set; } = new();
    public List<DayActivity> ActivityHeatmap { get; set; } = new();
    public List<FrequencyBarItem> FrequencyBars { get; set; } = new();
    public List<TopMediaItem> TopSeries { get; set; } = new();
    public List<TopMediaItem> TopMovies { get; set; } = new();
}

public class TopMediaItem
{
    public string Title { get; set; } = string.Empty;
    public int PlayCount { get; set; }
    public double TotalHours { get; set; }
    public string Type { get; set; } = "Movie"; // "Movie" or "Series"
    public string FormattedInfo => Type == "Series" ? $"{PlayCount} eps • {TotalHours:F1}h" : $"{PlayCount} plays • {TotalHours:F1}h";
}

public class FrequencyBarItem
{
    public string Label { get; set; } = string.Empty;
    public double Hours { get; set; }
    public double HeightRatio { get; set; } // 0.0 to 1.0 for UI bars
}

public class GenreCount
{
    public string Genre { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Hours { get; set; }
    public double Percentage { get; set; }
}

public class DayActivity
{
    public string Date { get; set; } = string.Empty; // YYYY-MM-DD
    public int PlaysCount { get; set; }
    public long DurationSeconds { get; set; }
    public int Level { get; set; } // 0, 1, 2, 3, 4 (GitHub-style intensity quantile)
    public string Tooltip => $"{Date}: {PlaysCount} items ({DurationSeconds / 3600.0:F1} hrs)";
}
