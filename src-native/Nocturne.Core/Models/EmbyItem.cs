using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

public class EmbyItem
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? OriginalTitle { get; set; }
    public string Type { get; set; } = string.Empty; // Movie, Series, Season, Episode, BoxSet, Folder
    public string? Overview { get; set; }
    public long? RunTimeTicks { get; set; }
    public int? ProductionYear { get; set; }
    public float? CommunityRating { get; set; }
    public int? IndexNumber { get; set; } // Episode number
    public int? ParentIndexNumber { get; set; } // Season number
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    public string? SeasonId { get; set; }
    public string? SeasonName { get; set; }
    public string? OfficialRating { get; set; }

    public Dictionary<string, string>? ProviderIds { get; set; }
    public Dictionary<string, string>? ImageTags { get; set; }
    public List<string>? BackdropImageTags { get; set; }
    public List<string>? Genres { get; set; }
    public List<string>? Studios { get; set; }
    public List<string>? Taglines { get; set; }
    public List<BaseItemPerson>? People { get; set; }
    public List<MediaUrl>? RemoteTrailers { get; set; }

    public UserItemData? UserData { get; set; }
    public List<MediaSourceInfo>? MediaSources { get; set; }

    // Multi-server or local metadata
    [JsonIgnore]
    public string? ServerId { get; set; }
    [JsonIgnore]
    public string? ServerName { get; set; }
    [JsonIgnore]
    public string? DedupGroupId { get; set; }
    [JsonIgnore]
    public int VersionCount { get; set; } = 1;

    // Helper properties for UI
    [JsonIgnore]
    public string DisplayTitle => Type switch
    {
        "Episode" when ParentIndexNumber.HasValue && IndexNumber.HasValue =>
            $"S{ParentIndexNumber:D2}E{IndexNumber:D2} - {Name}",
        _ => Name
    };

    [JsonIgnore]
    public string SubtitleText => Type switch
    {
        "Episode" => SeriesName ?? string.Empty,
        "Movie" when ProductionYear.HasValue => $"{ProductionYear}",
        "Series" when ProductionYear.HasValue => $"{ProductionYear}",
        _ => string.Empty
    };

    [JsonIgnore]
    public bool IsPlayed => UserData?.Played ?? false;

    [JsonIgnore]
    public string TmdbId => ProviderIds?.GetValueOrDefault("Tmdb")
                         ?? ProviderIds?.GetValueOrDefault("TheMovieDb") ?? string.Empty;

    [JsonIgnore]
    public string ImdbId => ProviderIds?.GetValueOrDefault("Imdb") ?? string.Empty;

    [JsonIgnore]
    public string TvdbId => ProviderIds?.GetValueOrDefault("Tvdb")
                         ?? ProviderIds?.GetValueOrDefault("TheTVDB") ?? string.Empty;

    [JsonIgnore]
    public string ResolutionBadge
    {
        get
        {
            var vid = MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
            if (vid == null) return string.Empty;
            if (vid.Width >= 3800 || vid.Height >= 2100) return "4K UHD";
            if (vid.Width >= 1900 || vid.Height >= 1000) return "1080p";
            if (vid.Width >= 1200 || vid.Height >= 700) return "720p";
            return "SD";
        }
    }

    [JsonIgnore]
    public string CodecBadge
    {
        get
        {
            var vid = MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
            return vid?.Codec?.ToUpperInvariant() ?? string.Empty;
        }
    }

    [JsonIgnore]
    public double ProgressPercentage
    {
        get
        {
            if (UserData == null || !RunTimeTicks.HasValue || RunTimeTicks.Value <= 0)
                return 0.0;
            return Math.Clamp((double)UserData.PlaybackPositionTicks / RunTimeTicks.Value * 100.0, 0.0, 100.0);
        }
    }
}

public class BaseItemPerson
{
    public string? Name { get; set; }
    public string? Role { get; set; }
    public string? Type { get; set; } // Actor, Director, Writer
    public string? PrimaryImageTag { get; set; }
    public string? Id { get; set; }
}

public class MediaUrl
{
    public string? Url { get; set; }
    public string? Name { get; set; }
}

public class UserItemData
{
    public long PlaybackPositionTicks { get; set; }
    public int PlayCount { get; set; }
    public bool Played { get; set; }
    public bool IsFavorite { get; set; }
    public DateTime? LastPlayedDate { get; set; }
}

public class MediaSourceInfo
{
    public string Id { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Container { get; set; }
    public long? Size { get; set; }
    public long? Bitrate { get; set; }
    public List<MediaStreamInfo>? MediaStreams { get; set; }
}

public class MediaStreamInfo
{
    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? DisplayTitle { get; set; }
    public string? Type { get; set; } // Video, Audio, Subtitle
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Index { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}

public class ItemsResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalRecordCount { get; set; }
}
