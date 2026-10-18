using System.Text.RegularExpressions;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class DedupGroup
{
    public string GroupId { get; set; } = Guid.NewGuid().ToString("N");
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int? Year { get; set; }
    public string Type { get; set; } = "Movie"; // Movie or Series
    public EmbyItem PrimaryItem { get; set; } = null!;
    public List<EmbyItem> SiblingItems { get; set; } = new();

    public int VersionCount => 1 + SiblingItems.Count;
    public List<EmbyItem> AllVersions
    {
        get
        {
            var list = new List<EmbyItem> { PrimaryItem };
            list.AddRange(SiblingItems);
            return list;
        }
    }
}

public class DedupEngine
{
    private static readonly Regex PunctuationRegex = new(@"[^\w\d\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    public static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var stripped = PunctuationRegex.Replace(title.ToLowerInvariant(), " ");
        return MultiSpaceRegex.Replace(stripped, " ").Trim();
    }

    public List<DedupGroup> Deduplicate(IEnumerable<EmbyItem> items)
    {
        var allItemsList = items.ToList();
        var groups = new List<DedupGroup>();
        var groupedItemIds = new HashSet<string>();

        // Separate Movies and Series
        var movies = allItemsList.Where(i => i.Type == "Movie").ToList();
        var series = allItemsList.Where(i => i.Type == "Series").ToList();

        // ── Pass 1: TMDB Movie ───────────────────────────
        RunMatchingPass(movies, groups, groupedItemIds, "Movie", i => !string.IsNullOrEmpty(i.TmdbId) ? $"tmdb_{i.TmdbId}" : null);

        // ── Pass 2: IMDB Movie ───────────────────────────
        RunMatchingPass(movies, groups, groupedItemIds, "Movie", i => !string.IsNullOrEmpty(i.ImdbId) ? $"imdb_{i.ImdbId}" : null);

        // ── Pass 3: Name + Year Movie ─────────────────────
        RunMatchingPass(movies, groups, groupedItemIds, "Movie", i =>
        {
            var norm = NormalizeTitle(i.Name);
            return !string.IsNullOrEmpty(norm) ? $"name_{norm}_{i.ProductionYear ?? 0}" : null;
        });

        // ── Pass 4: TMDB Series ──────────────────────────
        RunMatchingPass(series, groups, groupedItemIds, "Series", i => !string.IsNullOrEmpty(i.TmdbId) ? $"tmdb_{i.TmdbId}" : null);

        // ── Pass 5: IMDB Series ──────────────────────────
        RunMatchingPass(series, groups, groupedItemIds, "Series", i => !string.IsNullOrEmpty(i.ImdbId) ? $"imdb_{i.ImdbId}" : null);

        // ── Pass 6: Name + Year Series ────────────────────
        RunMatchingPass(series, groups, groupedItemIds, "Series", i =>
        {
            var norm = NormalizeTitle(i.Name);
            return !string.IsNullOrEmpty(norm) ? $"name_{norm}_{i.ProductionYear ?? 0}" : null;
        });

        // Add remaining un-grouped items as singleton groups (so they are searchable and displayable)
        foreach (var item in allItemsList.Where(i => !groupedItemIds.Contains(i.Id)))
        {
            groups.Add(new DedupGroup
            {
                GroupId = Guid.NewGuid().ToString("N"),
                TmdbId = item.TmdbId,
                ImdbId = item.ImdbId,
                Title = item.Name,
                Year = item.ProductionYear,
                Type = item.Type,
                PrimaryItem = item,
                SiblingItems = new List<EmbyItem>()
            });
        }

        // Apply primary scoring & version sorting for each group
        foreach (var group in groups)
        {
            if (group.SiblingItems.Count > 0)
            {
                var allCopies = group.AllVersions;
                var best = allCopies.OrderByDescending(ScorePrimaryItem).First();
                group.PrimaryItem = best;
                group.SiblingItems = allCopies.Where(c => c.Id != best.Id)
                                              .OrderByDescending(ScorePrimaryItem)
                                              .ToList();

                // Annotate items with group info
                group.PrimaryItem.DedupGroupId = group.GroupId;
                group.PrimaryItem.VersionCount = group.VersionCount;
                foreach (var sib in group.SiblingItems)
                {
                    sib.DedupGroupId = group.GroupId;
                    sib.VersionCount = group.VersionCount;
                }
            }
        }

        NocturneLogger.Info("DedupEngine", $"Deduplication complete: {allItemsList.Count} items -> {groups.Count} unique groups ({groups.Count(g => g.SiblingItems.Count > 0)} multi-version groups).");
        return groups;
    }

    private void RunMatchingPass(
        List<EmbyItem> pool,
        List<DedupGroup> groups,
        HashSet<string> groupedItemIds,
        string type,
        Func<EmbyItem, string?> keySelector)
    {
        var bucket = new Dictionary<string, List<EmbyItem>>();

        foreach (var item in pool)
        {
            if (groupedItemIds.Contains(item.Id)) continue;
            var key = keySelector(item);
            if (string.IsNullOrEmpty(key)) continue;

            if (!bucket.TryGetValue(key, out var list))
            {
                list = new List<EmbyItem>();
                bucket[key] = list;
            }
            list.Add(item);
        }

        foreach (var kv in bucket)
        {
            if (kv.Value.Count > 1)
            {
                var group = new DedupGroup
                {
                    GroupId = Guid.NewGuid().ToString("N"),
                    TmdbId = kv.Value.FirstOrDefault(i => !string.IsNullOrEmpty(i.TmdbId))?.TmdbId,
                    ImdbId = kv.Value.FirstOrDefault(i => !string.IsNullOrEmpty(i.ImdbId))?.ImdbId,
                    Title = kv.Value[0].Name,
                    Year = kv.Value[0].ProductionYear,
                    Type = type,
                    PrimaryItem = kv.Value[0],
                    SiblingItems = kv.Value.Skip(1).ToList()
                };

                foreach (var item in kv.Value)
                {
                    groupedItemIds.Add(item.Id);
                }

                groups.Add(group);
            }
        }
    }

    public static int ScorePrimaryItem(EmbyItem item)
    {
        int score = 0;
        if (item.BackdropImageTags != null && item.BackdropImageTags.Count > 0) score += 3;
        if (item.ImageTags != null && item.ImageTags.ContainsKey("Primary")) score += 2;
        if (!string.IsNullOrWhiteSpace(item.Overview)) score += 2;
        if (item.CommunityRating.HasValue && item.CommunityRating > 0) score += 1;
        if (item.Genres != null && item.Genres.Count > 0) score += 1;
        if (item.Studios != null && item.Studios.Count > 0) score += 1;

        // Resolution score: 4K (+4), 1080p (+2), 720p (+1)
        var stream = item.MediaSources?.FirstOrDefault()?.MediaStreams?.FirstOrDefault(s => s.Type == "Video");
        if (stream != null)
        {
            if (stream.Width >= 3800 || stream.Height >= 2100) score += 4;
            else if (stream.Width >= 1900 || stream.Height >= 1000) score += 2;
            else if (stream.Width >= 1200 || stream.Height >= 700) score += 1;
        }

        return score;
    }

    public int RebuildIndex(DatabaseService db)
    {
        var allItems = db.GetCachedItems(limit: 50000);
        var groups = Deduplicate(allItems);
        db.SaveDedupGroups(groups);
        return groups.Count(g => g.SiblingItems.Count > 0);
    }
}
