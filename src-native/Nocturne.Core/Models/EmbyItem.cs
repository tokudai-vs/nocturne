using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nocturne.Core.Models;

public class EmbyItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    [JsonConverter(typeof(FlexibleStringConverter))]
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SortName { get; set; }
    public string? OriginalTitle { get; set; }
    public string Type { get; set; } = string.Empty; // Movie, Series, Season, Episode, BoxSet, Folder
    public string? CollectionType { get; set; } // movies, tvshows, music, etc.
    public string? Overview { get; set; }
    public long? RunTimeTicks { get; set; }
    public int? ProductionYear { get; set; }
    public float? CommunityRating { get; set; }
    public int? IndexNumber { get; set; } // Episode number
    public int? ParentIndexNumber { get; set; } // Season number
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SeriesId { get; set; }
    public string? SeriesName { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? SeasonId { get; set; }
    public string? SeasonName { get; set; }
    public string? OfficialRating { get; set; }
    public DateTime? PremiereDate { get; set; }

    public Dictionary<string, string>? ProviderIds { get; set; }
    public Dictionary<string, string>? SeriesProviderIds { get; set; }
    public Dictionary<string, string>? ImageTags { get; set; }
    public List<string>? BackdropImageTags { get; set; }
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Genres { get; set; }
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Studios { get; set; }
    [JsonConverter(typeof(FlexibleStringListConverter))]
    public List<string>? Taglines { get; set; }
    public List<BaseItemPerson>? People { get; set; }
    public List<MediaUrl>? RemoteTrailers { get; set; }

    private UserItemData? _userData;
    public UserItemData? UserData
    {
        get => _userData;
        set
        {
            _userData = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsPlayed));
            OnPropertyChanged(nameof(ProgressPercentage));
            OnPropertyChanged(nameof(HasResumeProgress));
        }
    }

    public List<MediaSourceInfo>? MediaSources { get; set; }
    public List<ChapterInfo>? Chapters { get; set; }

    // Multi-server or local metadata
    [JsonIgnore]
    public string? PrimaryImageUrl { get; set; }
    [JsonIgnore]
    public string? ServerId { get; set; }
    [JsonIgnore]
    public string? ServerName { get; set; }
    [JsonIgnore]
    public string? DedupGroupId { get; set; }
    [JsonIgnore]
    public string? SeriesTmdbId => SeriesProviderIds?.GetValueOrDefault("Tmdb") ?? SeriesProviderIds?.GetValueOrDefault("TheMovieDb");

    [JsonIgnore]
    public string? SeriesTvdbId => SeriesProviderIds?.GetValueOrDefault("Tvdb") ?? SeriesProviderIds?.GetValueOrDefault("TheTVDB");

    private string? _seriesImdbId;
    [JsonIgnore]
    public string? SeriesImdbId
    {
        get => _seriesImdbId ?? SeriesProviderIds?.GetValueOrDefault("Imdb");
        set => _seriesImdbId = value;
    }
    [JsonIgnore]
    public int VersionCount { get; set; } = 1;

    // Helper properties for UI
    [JsonIgnore]
    public string DisplayTitle => Type switch
    {
        "Episode" when ParentIndexNumber.HasValue && IndexNumber.HasValue =>
            $"S{ParentIndexNumber:D2}E{IndexNumber:D2} - {Name}",
        "Episode" when IndexNumber.HasValue =>
            $"E{IndexNumber:D2} - {Name}",
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
    public string? ImageTag => ImageTags?.GetValueOrDefault("Primary");

    [JsonIgnore]
    public string? ImageServerId { get; set; }

    [JsonIgnore]
    public string? ImageItemId { get; set; }

    [JsonIgnore]
    public string? TmdbPosterPath { get; set; }

    [JsonIgnore]
    public bool IsInWatchlist { get; set; }

    [JsonIgnore]
    public string? BackdropTag => BackdropImageTags?.FirstOrDefault();

    [JsonIgnore]
    public string? FirstTagline => Taglines?.FirstOrDefault();

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
            var w = Width;
            var h = Height;
            if (w == null && h == null)
            {
                var vid = MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
                if (vid != null)
                {
                    w = vid.Width;
                    h = vid.Height;
                }
            }
            if (w == null && h == null) return string.Empty;
            if ((w.HasValue && w.Value >= 3800) || (h.HasValue && h.Value >= 2100)) return "4K UHD";
            if ((w.HasValue && w.Value >= 1900) || (h.HasValue && h.Value >= 1000)) return "1080p";
            if ((w.HasValue && w.Value >= 1200) || (h.HasValue && h.Value >= 700)) return "720p";
            return "SD";
        }
    }

    private int? _height;
    [JsonIgnore]
    public int? Height
    {
        get => _height ?? MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video")?.Height;
        set => _height = value;
    }

    private int? _width;
    [JsonIgnore]
    public int? Width
    {
        get => _width ?? MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video")?.Width;
        set => _width = value;
    }

    private string? _videoCodec;
    [JsonIgnore]
    public string? VideoCodec
    {
        get => _videoCodec ?? MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video")?.Codec;
        set => _videoCodec = value;
    }

    private string? _audioCodec;
    [JsonIgnore]
    public string? AudioCodec
    {
        get => _audioCodec ?? MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Audio")?.Codec;
        set => _audioCodec = value;
    }

    private long? _sizeBytes;
    [JsonIgnore]
    public long? SizeBytes
    {
        get => _sizeBytes ?? MediaSources?.FirstOrDefault()?.Size;
        set => _sizeBytes = value;
    }

    private long? _bitrate;
    [JsonIgnore]
    public long? Bitrate
    {
        get => _bitrate ?? MediaSources?.FirstOrDefault()?.Bitrate;
        set => _bitrate = value;
    }

    [JsonIgnore]
    public int? RuntimeSeconds => RunTimeTicks.HasValue && RunTimeTicks.Value > 0 ? (int)(RunTimeTicks.Value / 10_000_000L) : null;

    [JsonIgnore]
    public long? MeasuredBitrate
    {
        get
        {
            if (_bitrate.HasValue && _bitrate.Value > 0)
                return _bitrate.Value;
            var src = MediaSources?.FirstOrDefault();
            if (src?.Bitrate.HasValue == true && src.Bitrate.Value > 0)
                return src.Bitrate.Value;
            if (src?.Size.HasValue == true && src.Size.Value > 0 && RuntimeSeconds.HasValue && RuntimeSeconds.Value > 0)
                return (src.Size.Value * 8L) / RuntimeSeconds.Value;
            if (SizeBytes.HasValue && SizeBytes.Value > 0 && RuntimeSeconds.HasValue && RuntimeSeconds.Value > 0)
                return (SizeBytes.Value * 8L) / RuntimeSeconds.Value;
            return null;
        }
    }

    [JsonIgnore]
    public string BitrateBadge
    {
        get
        {
            var br = MeasuredBitrate;
            if (!br.HasValue || br.Value <= 0) return string.Empty;
            double mbps = br.Value / 1_000_000.0;
            return mbps >= 1.0 ? $"{mbps:0.#} Mbps" : $"{br.Value / 1_000:N0} kbps";
        }
    }

    [JsonIgnore]
    public string Edition
    {
        get
        {
            var path = MediaSources?.FirstOrDefault()?.Path ?? Name ?? string.Empty;
            var upper = path.ToUpperInvariant();
            if (upper.Contains("IMAX")) return "IMAX";
            if (upper.Contains("REMUX")) return "Remux";
            if (upper.Contains("BLURAY") || upper.Contains("BLU-RAY") || upper.Contains("BDMV")) return "BluRay";
            if (upper.Contains("WEB-DL") || upper.Contains("WEBDL") || upper.Contains("WEBRIP")) return "WEB-DL";
            if (upper.Contains("HDTV")) return "HDTV";
            return string.Empty;
        }
    }

    [JsonIgnore]
    public string HdrType
    {
        get
        {
            var path = (MediaSources?.FirstOrDefault()?.Path ?? string.Empty) + " " + (Name ?? string.Empty);
            var upper = path.ToUpperInvariant();
            if (upper.Contains("DOVI") || upper.Contains("DOLBY VISION") || upper.Contains(".DV.")) return "DV";
            if (upper.Contains("HDR10+") || upper.Contains("HDR10PLUS")) return "HDR10+";
            if (upper.Contains("HDR10") || upper.Contains("HDR")) return "HDR10";
            return "SDR";
        }
    }

    [JsonIgnore]
    public string TechnicalBadge
    {
        get
        {
            var res = ResolutionBadge; // "4K UHD", "1080p", "720p", "SD"
            var ed = Edition; // "IMAX", "Remux", "BluRay", "WEB-DL"
            var hdr = HdrType; // "DV", "HDR10", "SDR"

            string resPart = res switch
            {
                "4K UHD" => "4K",
                "1080p" => "1080p",
                "720p" => "720p",
                _ => "SD"
            };

            string edPart = !string.IsNullOrEmpty(ed) ? ed : (resPart == "4K" ? "UHD" : "HD");
            if (ed == "IMAX") edPart = "IMAX Enhanced";

            string badge = $"{resPart} {edPart}";
            if (hdr == "DV") badge += " • Dolby Vision";
            else if (hdr == "HDR10+" || hdr == "HDR10") badge += " • HDR";

            var audio = AudioCodec;
            if (!string.IsNullOrEmpty(audio))
            {
                var audioUpper = audio.ToUpperInvariant();
                if (audioUpper.Contains("TRUEHD") || audioUpper.Contains("ATMOS")) badge += " • Atmos";
                else if (audioUpper.Contains("DTS-HD") || audioUpper.Contains("DTS")) badge += " • DTS";
            }

            return badge.Trim();
        }
    }

    [JsonIgnore]
    public List<EmbyItem> FallbackCopies { get; set; } = new();

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

    [JsonIgnore]
    public bool HasResumeProgress => !IsPlayed && ProgressPercentage > 0;

    public override bool Equals(object? obj)
    {
        if (obj is EmbyItem other)
        {
            if (ReferenceEquals(this, other)) return true;
            if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(other.Id)) return false;
            return string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ServerId, other.ServerId, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Id?.ToLowerInvariant(), ServerId?.ToLowerInvariant());
    }
}

public class BaseItemPerson
{
    public string? Name { get; set; }
    public string? Role { get; set; }
    public string? Type { get; set; } // Actor, Director, Writer
    public string? PrimaryImageTag { get; set; }
    [JsonConverter(typeof(FlexibleStringConverter))]
    public string? Id { get; set; }
    [JsonIgnore]
    public string? ImageUrl { get; set; }
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
    [JsonConverter(typeof(FlexibleStringConverter))]
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
    public int? Channels { get; set; }
    public int? Index { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
}

public class ChapterInfo
{
    [JsonPropertyName("StartPositionTicks")]
    public long StartPositionTicks { get; set; }

    [JsonPropertyName("Name")]
    public string? Name { get; set; }

    [JsonPropertyName("ImageTag")]
    public string? ImageTag { get; set; }

    [JsonPropertyName("ImagePath")]
    public string? ImagePath { get; set; }
}

public class ItemsResult<T>
{
    public List<T> Items { get; set; } = new();
    public int TotalRecordCount { get; set; }
    [JsonIgnore]
    public bool IsSuccess { get; set; } = true;
}

public class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var l) ? l.ToString() : reader.GetDouble().ToString(CultureInfo.InvariantCulture),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}

public class FlexibleStringListConverter : JsonConverter<List<string>>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        var list = new List<string>();

        if (reader.TokenType == JsonTokenType.String)
        {
            var str = reader.GetString();
            if (!string.IsNullOrEmpty(str)) list.Add(str);
            return list;
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var s = reader.GetString();
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                else if (reader.TokenType == JsonTokenType.Number)
                {
                    list.Add(reader.GetInt64().ToString());
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    string? name = null;
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                    {
                        if (reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var prop = reader.GetString();
                            reader.Read();
                            if (string.Equals(prop, "Name", StringComparison.OrdinalIgnoreCase))
                            {
                                if (reader.TokenType == JsonTokenType.String)
                                    name = reader.GetString();
                                else if (reader.TokenType == JsonTokenType.Number)
                                    name = reader.GetInt64().ToString();
                            }
                            else
                            {
                                reader.TrySkip();
                            }
                        }
                    }
                    if (!string.IsNullOrEmpty(name))
                    {
                        list.Add(name);
                    }
                }
                else
                {
                    reader.TrySkip();
                }
            }
            return list;
        }

        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}
