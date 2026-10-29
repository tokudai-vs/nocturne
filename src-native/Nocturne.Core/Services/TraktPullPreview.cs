using System.Text;

namespace Nocturne.Core.Services;

// #44: what a Trakt watched pull would mark, decided by the pull's own code (TraktSyncService.Decide) and written
// nowhere, so a person can check a sample against trakt.tv before accepting thousands of writes.
public sealed class TraktPullPreview
{
    public sealed record Row(string Kind, string Title, int? Season, int? Episode, string ServerId, string ItemId);

    // A group the cascade holds back: only its primary is marked (Marked empty when the primary is already played).
    public sealed record Held(int GroupId, string Title, IReadOnlyList<(string ServerId, string ItemId)> Marked, IReadOnlyList<(string ServerId, string ItemId)> NotMarked);

    public DateTime CreatedUtc { get; } = DateTime.UtcNow;
    public List<Row> Rows { get; } = new();
    public List<Held> HeldGroups { get; } = new();
    public int MoviesListed, MoviesInLibrary, MoviePages;
    public string? MoviesTrakt;
    public int ShowsListed, ShowPages, EpisodesListed, EpisodesInLibrary, SpecialsListed, SpecialsInLibrary, ShowsWithoutSeasons;
    public string? ShowsTrakt;
    public string? ShapeWithoutSeasons; // the first such show's fields, as Trakt sent them

    public string Report(Func<string, string> serverName)
    {
        var sb = new StringBuilder();
        var movies = Rows.Where(r => r.Kind == "movie").ToList();
        var episodes = Rows.Where(r => r.Kind == "episode").ToList();
        sb.AppendLine($"Trakt pull preview, {CreatedUtc:u}. Nothing was written.");
        sb.AppendLine($"Movies: {MoviesListed:N0} listed ({MoviePages} page(s), Trakt counts {MoviesTrakt ?? "?"}); {MoviesInLibrary:N0} in the library; {movies.Count:N0} version(s) would be marked.");
        sb.AppendLine($"Episodes: {ShowsListed:N0} show(s) ({ShowPages} page(s), Trakt counts {ShowsTrakt ?? "?"}); {EpisodesListed:N0} episode(s) listed, " +
                      $"{SpecialsListed:N0} of them specials (season 0); {EpisodesInLibrary:N0} in the library; {episodes.Count:N0} version(s) would be marked.");
        if (ShowsWithoutSeasons > 0)
            sb.AppendLine($"WARNING: {ShowsWithoutSeasons:N0} show(s) arrived without season data, shaped: {ShapeWithoutSeasons}. Their episodes cannot be decided.");
        if (EpisodesListed > 0 && SpecialsListed == 0)
            sb.AppendLine("WARNING: no season 0 episodes in the payload. Trakt has an open bug dropping specials from extended=progress; watched specials would be missing.");

        sb.AppendLine().AppendLine("Would mark, per server:");
        foreach (var srv in Rows.GroupBy(r => r.ServerId).OrderBy(g => serverName(g.Key)))
            sb.AppendLine($"  {serverName(srv.Key)}: {srv.Count(r => r.Kind == "movie"):N0} movie version(s), {srv.Count(r => r.Kind == "episode"):N0} episode version(s)");

        sb.AppendLine().AppendLine("Episodes per show (would mark):");
        foreach (var show in episodes.GroupBy(r => r.Title).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            sb.AppendLine($"  {show.Key}: " + string.Join(", ", show.GroupBy(r => r.ServerId).OrderBy(g => serverName(g.Key)).Select(g => $"{serverName(g.Key)} {g.Count():N0}")));

        sb.AppendLine().AppendLine($"Held groups ({HeldGroups.Count:N0}): the cascade is held back, so only the primary is marked:");
        foreach (var h in HeldGroups)
            sb.AppendLine($"  group {h.GroupId} '{h.Title}': marks {(h.Marked.Count == 0 ? "nothing (primary already played)" : Versions(h.Marked))}; not marked: {Versions(h.NotMarked)}");

        sb.AppendLine().AppendLine("First 50 episode rows (show | season | episode | server | item id):");
        foreach (var r in episodes.Take(50))
            sb.AppendLine($"  {r.Title} | S{r.Season:D2} | E{r.Episode:D2} | {serverName(r.ServerId)} | {r.ItemId}");
        sb.AppendLine().AppendLine("First 10 movie rows (title | server | item id):");
        foreach (var r in movies.Take(10))
            sb.AppendLine($"  {r.Title} | {serverName(r.ServerId)} | {r.ItemId}");
        return sb.ToString();

        string Versions(IEnumerable<(string ServerId, string ItemId)> vs) => string.Join(", ", vs.Select(v => $"{serverName(v.ServerId)} {v.ItemId}"));
    }
}
