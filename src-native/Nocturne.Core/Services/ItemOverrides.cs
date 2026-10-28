namespace Nocturne.Core.Services;

// #38: Nocturne-side corrections to what a server says about one item, for servers nobody here can edit. Keyed on
// (server_id, item_id), which a rebuild keeps. A provider field replaces the server's id, or removes it when Value is
// null, before any dedup key is built. Cascade releases a group the sibling cascade holds back (#37): every member
// carries the same token, and a newcomer to the group holds it again.
public sealed record ItemOverride(string ServerId, string ItemId, string Field, string? Value, string CreatedUtc);

public static class ItemOverrides
{
    public const string Cascade = "Cascade";

    // Emby's ProviderIds names, with the aliases EmbyItem.ProviderId also reads.
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Tvdb"] = new[] { "Tvdb", "TheTVDB" },
        ["Tmdb"] = new[] { "Tmdb", "TheMovieDb" },
        ["Imdb"] = new[] { "Imdb" },
    };

    public static bool IsProviderField(string field) => Aliases.ContainsKey(field);

    public static string? Read(Dictionary<string, string>? ids, string field) =>
        Models.EmbyItem.ProviderId(ids, Aliases[field][0], Aliases[field].Length > 1 ? Aliases[field][1] : null);

    // The ids an item carries under its overrides, and the server's own ids to keep beside them (null when none
    // applies). serverIds is what an earlier application kept; an item fresh from the server has none, so its ids are
    // the server's. Applying twice gives the same result, and applying none restores the server's ids.
    public static (Dictionary<string, string>? Ids, Dictionary<string, string>? ServerIds) Apply(
        Dictionary<string, string>? ids, Dictionary<string, string>? serverIds, IEnumerable<ItemOverride> overrides)
    {
        var server = serverIds ?? ids;
        var mine = overrides.Where(o => IsProviderField(o.Field)).ToList();
        if (mine.Count == 0) return (server, null);
        var result = server == null ? new Dictionary<string, string>() : new Dictionary<string, string>(server);
        foreach (var o in mine)
        {
            foreach (var key in result.Keys.Where(k => Aliases[o.Field].Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
                result.Remove(key);
            if (o.Value != null) result[o.Field] = o.Value;
        }
        return (result, server ?? new Dictionary<string, string>());
    }
}
