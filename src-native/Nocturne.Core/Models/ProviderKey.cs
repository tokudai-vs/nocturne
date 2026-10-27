using System.Text.RegularExpressions;

namespace Nocturne.Core.Models;

public enum Provider { Tmdb, Imdb, Tvdb }

// What a key identifies. SeasonEpisode: an episode by its series' id and its position (the key every server
// shares); Episode: an episode by its own id.
public enum KeyKind { Movie, Series, Episode, SeasonEpisode }

// A title's identity at one provider, and the only producer and parser of the key strings Nocturne stores and
// matches on (#28: three silent no-ops came from one side writing a string the other side never read).
// Two string forms:
//   ToString()  identity: dedup_keys.key, watchlist_entries.ref
//               tmdb:movie:550 · imdb:series:tt0903747 · tvdb:ep:349232 · ep:tvdb:81189:s01e01
//   TmdbKey     TMDB's: dedup_groups.tmdb_key, tmdb_metadata.provider_key, watchlist_entries.tmdb_key
//               tmdb:movie:550 · tmdb:tv:1396 · tmdb:tv:1396:s01e01   (null where TMDB has no lookup)
// Fuzzy dedup keys (ep:series:, ep:name:, name and item keys) are not provider keys and stay strings.
public readonly record struct ProviderKey
{
    public Provider Provider { get; }
    public KeyKind Kind { get; }
    public string Id { get; }   // a SeasonEpisode's is its series' id
    public int SeasonNumber { get; }
    public int EpisodeNumber { get; }

    private ProviderKey(Provider provider, KeyKind kind, string id, int season = 0, int episode = 0) =>
        (Provider, Kind, Id, SeasonNumber, EpisodeNumber) = (provider, kind, id, season, episode);

    public static IReadOnlyList<Provider> Providers { get; } = new[] { Provider.Tmdb, Provider.Imdb, Provider.Tvdb };

    // IMDb ids are tt + digits; TMDB and TVDB ids are digits; at most 18 digits, so every id fits a long (TraktId).
    // Anything else (slugs, paths, a tt id filed as TMDB, an overlong number) is rejected before it can become a key.
    private const int MaxDigits = 18;

    public static bool IsValidId(Provider provider, string? id)
    {
        var digits = provider == Provider.Imdb ? (id is { Length: > 2 } && id.StartsWith("tt", StringComparison.Ordinal) ? id[2..] : null) : id;
        return digits is { Length: > 0 and <= MaxDigits } && digits.All(char.IsAsciiDigit);
    }

    // Factories: null when the id is not valid for the provider (or the position is unknown).
    public static ProviderKey? Movie(Provider p, string? id) => Make(p, KeyKind.Movie, id);
    public static ProviderKey? Series(Provider p, string? id) => Make(p, KeyKind.Series, id);
    public static ProviderKey? Episode(Provider p, string? id) => Make(p, KeyKind.Episode, id);
    public static ProviderKey? SeasonEpisode(Provider p, string? seriesId, int? season, int? episode) =>
        season is >= 0 && episode is >= 0 && IsValidId(p, seriesId) ? new ProviderKey(p, KeyKind.SeasonEpisode, seriesId!, season.Value, episode.Value) : null;

    // A movie or series key by Emby item type.
    public static ProviderKey? Title(string? itemType, Provider p, string? id) =>
        string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase) ? Series(p, id) : Movie(p, id);

    private static ProviderKey? Make(Provider p, KeyKind kind, string? id) => IsValidId(p, id) ? new ProviderKey(p, kind, id!) : null;

    public string IdName => Name(Provider);
    private string KindName => Kind switch { KeyKind.Movie => "movie", KeyKind.Series => "series", _ => "ep" };
    private string Position => $"s{SeasonNumber:D2}e{EpisodeNumber:D2}";

    public override string ToString() => Kind == KeyKind.SeasonEpisode ? $"ep:{IdName}:{Id}:{Position}" : $"{IdName}:{KindName}:{Id}";

    // The dedup conflict guard compares ids slot by slot ("tmdb:movie", "tvdb:ep", "ep:tvdb"): two keys in one slot
    // with different ids are different titles. A SeasonEpisode's slot holds its series' id.
    public string Slot => Kind == KeyKind.SeasonEpisode ? $"ep:{IdName}" : $"{IdName}:{KindName}";

    public string? TmdbKey => Provider != Provider.Tmdb ? null : Kind switch
    {
        KeyKind.Movie => $"tmdb:movie:{Id}",
        KeyKind.Series => $"tmdb:tv:{Id}",
        KeyKind.SeasonEpisode => $"tmdb:tv:{Id}:{Position}",
        _ => null, // TMDB serves an episode only through its show and season, never by the episode's own id
    };

    // The TMDB API path that answers for the key. A SeasonEpisode is answered by its whole season (§10.3, M3).
    public string? TmdbPath => TmdbKey == null ? null : Kind switch
    {
        KeyKind.Movie => $"/movie/{Id}",
        KeyKind.Series => $"/tv/{Id}",
        _ => $"/tv/{Id}/season/{SeasonNumber}",
    };

    // As a Trakt ids object holds it: imdb a string, tmdb and tvdb numbers.
    public object TraktId => Provider == Provider.Imdb ? Id : long.Parse(Id);

    private static readonly Regex PositionForm = new(@"^s(\d+)e(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool TryParse(string? s, out ProviderKey key)
    {
        key = default;
        var parts = s?.Split(':');
        ProviderKey? parsed = parts switch
        {
            [var ep, var p, var id, var pos] when ep.Equals("ep", StringComparison.OrdinalIgnoreCase) && TryProvider(p, out var provider)
                => TryPosition(pos, out var season, out var episode) ? SeasonEpisode(provider, id, season, episode) : null,
            [var p, var kind, var id] when TryProvider(p, out var provider) => kind.ToLowerInvariant() switch
            {
                "movie" => Movie(provider, id),
                "series" => Series(provider, id),
                "ep" => Episode(provider, id),
                _ => null,
            },
            _ => null,
        };
        return Set(parsed, out key);
    }

    public static bool TryParseTmdbKey(string? s, out ProviderKey key)
    {
        key = default;
        var parts = s?.Split(':');
        ProviderKey? parsed = parts switch
        {
            [var t, var kind, var id] when IsTmdb(t) => kind.ToLowerInvariant() switch
            {
                "movie" => Movie(Provider.Tmdb, id),
                "tv" => Series(Provider.Tmdb, id),
                _ => null,
            },
            [var t, var tv, var id, var pos] when IsTmdb(t) && tv.Equals("tv", StringComparison.OrdinalIgnoreCase)
                => TryPosition(pos, out var season, out var episode) ? SeasonEpisode(Provider.Tmdb, id, season, episode) : null,
            _ => null,
        };
        return Set(parsed, out key);
    }

    // The SQL test for "a provider key" over a key column, for statements that filter before parsing.
    public static string SqlIsProviderKey(string column) =>
        "(" + string.Join(" OR ", Providers.SelectMany(p => new[] { $"{column} GLOB '{Name(p)}:*'", $"{column} GLOB 'ep:{Name(p)}:*'" })) + ")";

    private static string Name(Provider p) => p switch { Provider.Tmdb => "tmdb", Provider.Imdb => "imdb", _ => "tvdb" };

    // "s01e01"; false (never an exception) for a number that does not fit an int.
    private static bool TryPosition(string pos, out int season, out int episode)
    {
        season = episode = 0;
        return PositionForm.Match(pos) is { Success: true } m
               && int.TryParse(m.Groups[1].Value, out season) && int.TryParse(m.Groups[2].Value, out episode);
    }

    private static bool IsTmdb(string s) => s.Equals("tmdb", StringComparison.OrdinalIgnoreCase);

    private static bool TryProvider(string s, out Provider provider)
    {
        foreach (var p in Providers)
            if (s.Equals(Name(p), StringComparison.OrdinalIgnoreCase)) { provider = p; return true; }
        provider = default;
        return false;
    }

    private static bool Set(ProviderKey? parsed, out ProviderKey key)
    {
        key = parsed ?? default;
        return parsed.HasValue;
    }
}
