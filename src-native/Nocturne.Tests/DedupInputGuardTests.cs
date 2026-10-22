using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// Stage C: dedup inputs (null episode numbers, series provider ids, malformed ids) and the conflicting-id
// merge guard. Each case is one that produced over-merged groups in the live database.
public class DedupInputGuardTests
{
    private static EmbyItem Movie(string id, string server, string name, string? tmdb = null, string? tvdb = null, string? imdb = null, int? year = null)
    {
        var ids = new Dictionary<string, string>();
        if (tmdb != null) ids["Tmdb"] = tmdb;
        if (tvdb != null) ids["Tvdb"] = tvdb;
        if (imdb != null) ids["Imdb"] = imdb;
        return new EmbyItem { Id = id, ServerId = server, Name = name, Type = "Movie", ProviderIds = ids, ProductionYear = year };
    }

    private static EmbyItem Episode(string id, string server, int? season, int? number, string? tvdbEp = null, string seriesId = "s1", string seriesName = "Jeopardy!")
    {
        var ids = new Dictionary<string, string>();
        if (tvdbEp != null) ids["Tvdb"] = tvdbEp;
        return new EmbyItem
        {
            Id = id, ServerId = server, Name = "Episode " + id, Type = "Episode", SeriesId = seriesId, SeriesName = seriesName,
            ParentIndexNumber = season, IndexNumber = number, ProviderIds = ids
        };
    }

    [Fact]
    public void EpisodeWithoutIndexNumber_GetsNoSeasonEpisodeKeys()
    {
        var keys = ConcurrentDedupIndex.GenerateCandidateKeys(Episode("e1", "a", 2021, null));
        Assert.DoesNotContain(keys, k => k.Key.Contains("s2021e"));

        var noSeason = ConcurrentDedupIndex.GenerateCandidateKeys(Episode("e2", "a", null, 4));
        Assert.DoesNotContain(noSeason, k => k.Key.Contains(":s01e04"));
    }

    [Fact]
    public void UnnumberedEpisodesWithDistinctIds_StaySeparate()
    {
        // The live 43-episode Jeopardy group: null IndexNumber -> shared ep:series:...:s2021e00 key.
        var index = new ConcurrentDedupIndex();
        var eps = Enumerable.Range(1, 5).Select(i => Episode($"e{i}", "a", 2021, null, tvdbEp: (9000 + i).ToString())).ToList();

        index.ProcessBatch(eps);

        Assert.Equal(5, eps.Select(e => e.DedupGroupId).Distinct().Count());
    }

    [Fact]
    public void ItemWithNoUsableKey_GetsAStableSingletonGroup()
    {
        var index = new ConcurrentDedupIndex();
        var first = Episode("e1", "a", null, null);
        index.ProcessBatch(new[] { first });
        var again = Episode("e1", "a", null, null);
        index.ProcessBatch(new[] { again });

        Assert.NotNull(first.DedupGroupId);
        Assert.Equal(first.DedupGroupId, again.DedupGroupId);
        Assert.Equal(1, index.TotalGroupsCount);
    }

    [Theory]
    [InlineData("Tmdb", "550", true)]
    [InlineData("Tmdb", "../tv/222031", false)]
    [InlineData("Tmdb", "101129-alerta-aeropuerto", false)]
    [InlineData("Tmdb", "tt1551630", false)]
    [InlineData("Tvdb", "81189", true)]
    [InlineData("Imdb", "tt0137523", true)]
    [InlineData("Imdb", "985423", false)]
    [InlineData("Imdb", "https://www.imdb.com/titl", false)]
    [InlineData("Imdb", "tt", false)]
    [InlineData("Tmdb", "", false)]
    public void ProviderIds_AreValidatedBeforeBecomingKeys(string provider, string value, bool valid)
    {
        Assert.Equal(valid, ConcurrentDedupIndex.IsValidProviderId(provider, value));
    }

    [Fact]
    public void ConflictingTmdbIds_RefuseMergeThroughASharedBadTvdbId()
    {
        // Group 1287 live: The Banker + Bunker joined by tvdb:movie:131759 while holding different TMDB ids.
        var index = new ConcurrentDedupIndex();
        var banker = Movie("m1", "valkyrie", "The Banker", tmdb: "500840", tvdb: "131759");
        var bunker = Movie("m2", "raptor", "Bunker", tmdb: "33333", tvdb: "131759");
        var banker2 = Movie("m3", "raptor", "The Banker", tmdb: "500840");

        index.ProcessBatch(new[] { banker, bunker, banker2 });

        Assert.NotEqual(banker.DedupGroupId, bunker.DedupGroupId);
        Assert.Equal(banker.DedupGroupId, banker2.DedupGroupId);
        Assert.Equal(1, index.ConflictsRefused);
    }

    [Fact]
    public void SuspectKey_IsDemoted_SoItNoLongerMergesAuthoritatively()
    {
        var index = new ConcurrentDedupIndex();
        var a = Movie("m1", "valkyrie", "The Banker", tmdb: "500840", tvdb: "131759");
        var b = Movie("m2", "raptor", "Bunker", tmdb: "33333", tvdb: "131759");
        index.ProcessBatch(new[] { a, b });

        // A third item linking both groups authoritatively must not fuse them.
        var bridge = Movie("m3", "raptor", "Bunker", tmdb: "33333", imdb: "tt0000001", tvdb: "131759");
        index.ProcessBatch(new[] { bridge });

        Assert.Equal(b.DedupGroupId, bridge.DedupGroupId);
        Assert.NotEqual(a.DedupGroupId, bridge.DedupGroupId);
        Assert.Equal(1, index.MultiVersionGroupsCount); // only Bunker's group has two versions
    }

    [Fact]
    public void DemotedKey_StillCountsAsConflictEvidence()
    {
        // Rebuilt group 8790: tmdb was demoted by an earlier refusal, then an item with a different tmdb id
        // attached through the shared imdb id because the group no longer "had" a tmdb id.
        var index = new ConcurrentDedupIndex();
        var p = Movie("p", "raptor", "The Seemingly Perfect Family", tmdb: "1229906", imdb: "tt38598321");
        var c = Movie("c", "raptor", "Other", tmdb: "1229906", imdb: "tt0000002"); // demotes tmdb:1229906 in p's group
        var b = Movie("b", "valkyrie", "The Seemingly Perfect Family", tmdb: "1648760", imdb: "tt38598321");

        index.ProcessBatch(new[] { p, c, b });

        Assert.NotEqual(p.DedupGroupId, b.DedupGroupId);
        Assert.Equal(2, index.ConflictsRefused);
    }

    [Fact]
    public void IdHeldByAnotherGroup_IsStillEvidence_AcrossAReload()
    {
        // Rebuilt group 32629: p's tmdb key belongs to another group (refused merge), so p's group never owned
        // it, and q with a different tmdb id attached through the shared imdb id.
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var h = Movie("h", "raptor", "Something Else", tmdb: "16183", imdb: "tt0000009");
            var p = Movie("p", "raptor", "Jhankaar Beats", tmdb: "16183", imdb: "tt0347278");
            foreach (var it in new[] { h, p }) db.UpsertItem(it, it.ServerId!);
            new ConcurrentDedupIndex().ProcessBatch(new[] { h, p }, db);
            Assert.NotEqual(h.DedupGroupId, p.DedupGroupId);

            var reloaded = new ConcurrentDedupIndex();
            reloaded.InitializeFromDb(db);
            var q = Movie("q", "valkyrie", "Jhankaar Beats", tmdb: "161834", imdb: "tt0347278");
            reloaded.ProcessBatch(new[] { q }, db);

            Assert.NotEqual(p.DedupGroupId, q.DedupGroupId);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    // #21 item 2: the pairs the guard split are listed with the ids they disagree on, for fixing on the server.
    [Fact]
    public void ConflictSplits_ListThePairTheGuardSplit_AndNotAMerge()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var r = Movie("r", "raptor", "The Great Hack", tmdb: "566222", imdb: "tt9358204", year: 2019);
            var v = Movie("v", "valkyrie", "The Great Hack", tmdb: "566222", imdb: "tt4736550", year: 2019);
            var a = Movie("a", "raptor", "Arrival", tmdb: "329865", imdb: "tt2543164");
            var b = Movie("b", "valkyrie", "Arrival", tmdb: "329865", imdb: "tt2543164");
            foreach (var it in new[] { r, v, a, b }) db.UpsertItem(it, it.ServerId!);
            new ConcurrentDedupIndex().ProcessBatch(new[] { r, v, a, b }, db);

            var split = Assert.Single(db.GetConflictPairs()!);

            Assert.Equal(("Movie", "The Great Hack", 2019, "imdb:movie", "tt9358204", "raptor", "tt4736550", "valkyrie"),
                         (split.ItemType, split.TitleA, split.YearA, split.Provider, split.IdsA, split.ServersA, split.IdsB, split.ServersB));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    // #22 item 1: episodes split because their series carries a conflicting id roll up into that series' row.
    [Fact]
    public void ConflictRollUp_MakesASeriesAndItsSplitEpisodesOneRow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            EmbyItem Show(string id, string srv, string tvdb) =>
                new() { Id = id, ServerId = srv, Name = "Hotel", Type = "Series", ProductionYear = 1983, ProviderIds = new() { ["Tvdb"] = tvdb, ["Tmdb"] = "1000" } };
            var shows = new[] { Show("rs", "raptor", "111"), Show("vs", "valkyrie", "222") };
            var episodes = Enumerable.Range(1, 3).SelectMany(e => new[]
            {
                Episode($"r{e}", "raptor", 1, e, seriesId: "rs", seriesName: "Hotel"),
                Episode($"v{e}", "valkyrie", 1, e, seriesId: "vs", seriesName: "Hotel"),
            }).ToList();
            foreach (var it in shows.Concat(episodes)) db.UpsertItem(it, it.ServerId!);
            var index = new ConcurrentDedupIndex();
            index.ProcessBatch(shows, db);
            index.ProcessBatch(episodes, db);
            var pairs = db.GetConflictPairs()!;
            Assert.Equal(4, pairs.Count); // the series and its three episodes

            var root = Assert.Single(DatabaseService.RollUpConflicts(pairs));

            Assert.Equal(("Series", "Hotel", 1983, "tvdb:series", "111", "222", 3, true),
                         (root.ItemType, root.Title, root.Year, root.Provider, root.IdsA, root.IdsB, root.Episodes, root.SeriesIds));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void AttachingItem_NeitherRepromotesNorStealsAnotherGroupsKeys()
    {
        var index = new ConcurrentDedupIndex();
        var p = Movie("p", "raptor", "Jhankaar Beats", tmdb: "16183", imdb: "tt0347278");
        var c = Movie("c", "raptor", "Other", tmdb: "16183", imdb: "tt0000002"); // demotes tmdb:16183 in p's group
        var again = Movie("p2", "valkyrie", "Jhankaar Beats", tmdb: "16183", imdb: "tt0347278");

        index.ProcessBatch(new[] { p, c, again });

        Assert.Equal(p.DedupGroupId, again.DedupGroupId);
        var (_, keys, _) = index.GetRebuildState();
        var tmdb = Assert.Single(keys, k => k.Key == "tmdb:movie:16183");
        Assert.False(tmdb.IsAuthoritative);
        Assert.Equal(int.Parse(p.DedupGroupId!), tmdb.GroupId);
        Assert.Equal(int.Parse(c.DedupGroupId!), Assert.Single(keys, k => k.Key == "imdb:movie:tt0000002").GroupId);
    }

    [Fact]
    public void ItemWhoseKeysAreAllDropped_ReingestsIntoTheSameGroup()
    {
        var index = new ConcurrentDedupIndex();
        index.ProcessBatch(new[] { Movie("g1", "a", "One", tmdb: "1", imdb: "tt0000001"), Movie("g2", "a", "Two", tmdb: "2", imdb: "tt0000002") });
        // Conflicts with g1 on imdb and with g2 on tmdb: both shared keys dropped, nothing left to claim.
        var x = Movie("x", "b", "Mixed", tmdb: "1", imdb: "tt0000002");
        index.ProcessBatch(new[] { x });
        var first = x.DedupGroupId;
        var groups = index.TotalGroupsCount;

        var again = Movie("x", "b", "Mixed", tmdb: "1", imdb: "tt0000002");
        index.ProcessBatch(new[] { again });

        Assert.Equal(first, again.DedupGroupId);
        Assert.Equal(groups, index.TotalGroupsCount);
    }

    [Fact]
    public void ConflictingEpisodeIds_RefuseMergeThroughASharedFallbackKey()
    {
        var index = new ConcurrentDedupIndex();
        var e1 = Episode("e1", "a", 1, 1, tvdbEp: "111");
        var e2 = Episode("e2", "a", 1, 1, tvdbEp: "222"); // same series + SxxEyy, different episode id

        index.ProcessBatch(new[] { e1, e2 });

        Assert.NotEqual(e1.DedupGroupId, e2.DedupGroupId);
    }

    [Fact]
    public void AgreeingIds_StillMergeAcrossServers()
    {
        var index = new ConcurrentDedupIndex();
        var a = Movie("m1", "valkyrie", "Arrival", tmdb: "329865", imdb: "tt2543164");
        var b = Movie("m2", "raptor", "Arrival (2016) [4K]", tmdb: "329865");

        index.ProcessBatch(new[] { a, b });

        Assert.Equal(a.DedupGroupId, b.DedupGroupId);
        Assert.Equal(0, index.ConflictsRefused);
    }

    [Fact]
    public void FuzzyAttachToAnAuthoritativeGroup_IsCapped()
    {
        // C5: an item reaching an authoritative group only through its fuzzy name+year key.
        var index = new ConcurrentDedupIndex();
        index.SetConnectedServerCount(2); // cap = 8
        var seed = Movie("seed", "valkyrie", "Hamlet", tmdb: "10549", year: 1996);
        index.ProcessBatch(new[] { seed });

        var fuzzy = Enumerable.Range(1, 10).Select(i => Movie($"f{i}", "raptor", "Hamlet", year: 1996)).ToList();
        index.ProcessBatch(fuzzy);

        var inSeedGroup = fuzzy.Count(f => f.DedupGroupId == seed.DedupGroupId);
        Assert.Equal(7, inSeedGroup); // seed + 7 = 8 versions, then rejected
    }

    [Fact]
    public void FuzzyAttach_RefusesAGroupWithADifferentIdOfTheSameProvider()
    {
        // Remake guard: same title + year, different TMDB id.
        var index = new ConcurrentDedupIndex();
        var original = Movie("m1", "valkyrie", "Dune", tmdb: "438631", year: 2021);
        var other = Movie("m2", "raptor", "Dune", tmdb: "999999", year: 2021);

        index.ProcessBatch(new[] { original, other });

        Assert.NotEqual(original.DedupGroupId, other.DedupGroupId);
    }

    [Fact]
    public void SeriesProviderIds_AreResolvedFromTheCachedSeriesRow_AndMatchAcrossServers()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            // Server-local series ids differ ("101" vs "7002"); the series' own Tvdb id is shared.
            db.UpsertItem(new EmbyItem { Id = "101", Name = "Severance", Type = "Series", ProviderIds = new() { { "Tvdb", "371980" } } }, "valkyrie");
            db.UpsertItem(new EmbyItem { Id = "7002", Name = "Severance", Type = "Series", ProviderIds = new() { { "Tvdb", "371980" } } }, "raptor");
            var a = Episode("ep_a", "valkyrie", 1, 3, seriesId: "101", seriesName: "Severance");
            var b = Episode("ep_b", "raptor", 1, 3, seriesId: "7002", seriesName: "Severance (US)");

            var index = new ConcurrentDedupIndex();
            index.ProcessBatch(new[] { a }, db);
            index.ProcessBatch(new[] { b }, db);

            Assert.Equal("371980", a.SeriesTvdbId);
            Assert.Contains(ConcurrentDedupIndex.GenerateCandidateKeys(b), k => k.Key == "ep:tvdb:371980:s01e03" && k.IsAuthoritative);
            Assert.Equal(a.DedupGroupId, b.DedupGroupId);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    // The 6 wrong groups on clean data: Raptor numbers an episode S02E10, Valkyrie numbers the same episode
    // S02E04 (both carry its Tvdb id), and an untagged Valkyrie file of a different episode, numbered S02E10,
    // matched the group by season/episode.
    [Fact]
    public void PositionalMatch_IsRefused_WhenTheSameServerNumbersItsMemberDifferently()
    {
        var index = new ConcurrentDedupIndex();
        var raptor = Episode("rx", "raptor", 2, 10, tvdbEp: "4001", seriesId: "r1", seriesName: "The Yogi Bear Show");
        var valkyrie = Episode("vx", "valkyrie", 2, 4, tvdbEp: "4001", seriesId: "v1", seriesName: "The Yogi Bear Show");
        var untagged = Episode("vz", "valkyrie", 2, 10, seriesId: "v2", seriesName: "The Yogi Bear Show (1961)");

        index.ProcessBatch(new[] { raptor, valkyrie, untagged });

        Assert.Equal(raptor.DedupGroupId, valkyrie.DedupGroupId); // renumbered across servers, same id: one episode
        Assert.NotEqual(raptor.DedupGroupId, untagged.DedupGroupId);
    }

    [Fact]
    public void BareFile_StillJoinsATaggedCopyOnAnotherServer()
    {
        var index = new ConcurrentDedupIndex();
        var tagged = Episode("rx", "raptor", 2, 10, tvdbEp: "4001", seriesId: "r1", seriesName: "The Yogi Bear Show");
        var bare = Episode("vz", "valkyrie", 2, 10, seriesId: "v2", seriesName: "The Yogi Bear Show");

        index.ProcessBatch(new[] { tagged, bare });

        Assert.Equal(tagged.DedupGroupId, bare.DedupGroupId);
    }

    [Fact]
    public void SameServerCopies_ShareNumbering_AndStillJoin()
    {
        var index = new ConcurrentDedupIndex();
        var raptor = Episode("rx", "raptor", 2, 10, tvdbEp: "4001", seriesId: "r1", seriesName: "The Yogi Bear Show");
        var hd = Episode("vx", "valkyrie", 2, 4, tvdbEp: "4001", seriesId: "v1", seriesName: "The Yogi Bear Show");
        var uhd = Episode("vx4k", "valkyrie", 2, 4, seriesId: "v1", seriesName: "The Yogi Bear Show"); // untagged 4K copy

        index.ProcessBatch(new[] { raptor, hd, uhd });

        Assert.Equal(hd.DedupGroupId, uhd.DedupGroupId);
        Assert.Equal(raptor.DedupGroupId, uhd.DedupGroupId);
    }

    [Fact]
    public void PositionalRefusal_HoldsAfterAReload()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var raptor = Episode("rx", "raptor", 2, 10, tvdbEp: "4001", seriesId: "r1", seriesName: "The Yogi Bear Show");
            var valkyrie = Episode("vx", "valkyrie", 2, 4, tvdbEp: "4001", seriesId: "v1", seriesName: "The Yogi Bear Show");
            foreach (var it in new[] { raptor, valkyrie }) db.UpsertItem(it, it.ServerId!);
            new ConcurrentDedupIndex().ProcessBatch(new[] { raptor, valkyrie }, db);

            var reloaded = new ConcurrentDedupIndex();
            reloaded.InitializeFromDb(db);
            var untagged = Episode("vz", "valkyrie", 2, 10, seriesId: "v2", seriesName: "The Yogi Bear Show");
            db.UpsertItem(untagged, "valkyrie");
            reloaded.ProcessBatch(new[] { untagged }, db);

            Assert.NotEqual(raptor.DedupGroupId, untagged.DedupGroupId);
        }
        finally
        {
            try { File.Delete(dbPath); } catch { }
        }
    }

    private static EmbyItem WithSeriesTvdb(EmbyItem episode, string seriesTvdb)
    {
        episode.SeriesProviderIds = new() { ["Tvdb"] = seriesTvdb };
        return episode;
    }

    // A rebuild resolves every series id up front, so both copies carry ep:tvdb keys. They differ only in
    // numbering, which once read as conflicting ids: the pair split and its shared episode id was demoted.
    [Fact]
    public void RenumberedCopies_WithSeriesIds_StillMergeOnTheEpisodeId()
    {
        var index = new ConcurrentDedupIndex();
        var raptor = WithSeriesTvdb(Episode("rx", "raptor", 2, 10, tvdbEp: "91051", seriesId: "r1", seriesName: "The Yogi Bear Show"), "72562");
        var valkyrie = WithSeriesTvdb(Episode("vx", "valkyrie", 2, 4, tvdbEp: "91051", seriesId: "v1", seriesName: "The Yogi Bear Show"), "72562");

        index.ProcessBatch(new[] { raptor, valkyrie });

        Assert.Equal(raptor.DedupGroupId, valkyrie.DedupGroupId);
        Assert.Equal(0, index.ConflictsRefused);
    }

    [Fact]
    public void SameNumbering_DifferentSeriesIds_StillConflict()
    {
        // Two shows normalising to one name ("Shameless" US / UK), same S01E01, no episode ids.
        var index = new ConcurrentDedupIndex();
        var us = WithSeriesTvdb(Episode("us", "raptor", 1, 1, seriesId: "r1", seriesName: "Shameless (US)"), "161511");
        var uk = WithSeriesTvdb(Episode("uk", "valkyrie", 1, 1, seriesId: "v1", seriesName: "Shameless (UK)"), "73185");

        index.ProcessBatch(new[] { us, uk });

        Assert.NotEqual(us.DedupGroupId, uk.DedupGroupId);
    }

    // Super Friends after a rebuild: with the renumbered pair split, Valkyrie's untagged S09E09 joined Raptor's
    // S09E09 alone; no Valkyrie member was there for the positional rule to see.
    [Fact]
    public void RenumberedPairStaysTogether_SoTheUntaggedCopyIsRefused()
    {
        var index = new ConcurrentDedupIndex();
        var raptor = WithSeriesTvdb(Episode("r", "raptor", 9, 9, tvdbEp: "4501235", seriesId: "r1", seriesName: "Super Friends"), "254222");
        var tagged = WithSeriesTvdb(Episode("vt", "valkyrie", 9, 7, tvdbEp: "4501235", seriesId: "v1", seriesName: "Super Friends"), "254222");
        var untagged = WithSeriesTvdb(Episode("vu", "valkyrie", 9, 9, seriesId: "v1", seriesName: "Super Friends"), "254222");

        index.ProcessBatch(new[] { raptor, tagged, untagged });

        Assert.Equal(raptor.DedupGroupId, tagged.DedupGroupId);
        Assert.NotEqual(raptor.DedupGroupId, untagged.DedupGroupId);
    }

    // 90 Day Diaries in sync order: Valkyrie's untagged S03E08 arrives before Valkyrie's tagged copy of the
    // episode Raptor numbers S03E08 (Valkyrie: S03E06), joins by number, and the tagged copy follows by id.
    private static (EmbyItem Raptor, EmbyItem Untagged, EmbyItem Tagged) UntaggedArrivingFirst() => (
        WithSeriesTvdb(Episode("r", "raptor", 3, 8, tvdbEp: "9057492", seriesId: "r1", seriesName: "90 Day Diaries"), "393180"),
        WithSeriesTvdb(Episode("vu", "valkyrie", 3, 8, seriesId: "v1", seriesName: "90 Day Diaries"), "393180"),
        WithSeriesTvdb(Episode("vt", "valkyrie", 3, 6, tvdbEp: "9057492", seriesId: "v1", seriesName: "90 Day Diaries"), "393180"));

    [Fact]
    public void UntaggedEpisodeArrivingFirst_IsPlacedAfterItsServersTaggedCopy()
    {
        var index = new ConcurrentDedupIndex();
        var (raptor, untagged, tagged) = UntaggedArrivingFirst();

        index.ProcessBatch(new[] { raptor, untagged, tagged }, untagged: ConcurrentDedupIndex.UntaggedEpisodes.Defer);
        Assert.Null(untagged.DedupGroupId);
        index.ProcessBatch(new[] { untagged });

        Assert.Equal(raptor.DedupGroupId, tagged.DedupGroupId);
        Assert.NotEqual(raptor.DedupGroupId, untagged.DedupGroupId);
    }

    // #20 item 3: during a sync the untagged episode is shown at once, in a group of its own that nothing can match
    // into, and the end pass places it exactly as a deferred one.
    [Fact]
    public void HeldUntaggedEpisode_HasAGroupAtOnce_AndIsPlacedAtTheEnd()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var (raptor, untagged, tagged) = UntaggedArrivingFirst();
            var index = new ConcurrentDedupIndex();
            var batch = new[] { raptor, untagged, tagged };

            index.ProcessBatch(batch, db, ConcurrentDedupIndex.UntaggedEpisodes.Hold);
            foreach (var it in batch) db.UpsertItem(it, it.ServerId!);

            var held = untagged.DedupGroupId;
            Assert.NotNull(held);
            Assert.NotEqual(raptor.DedupGroupId, held);
            Assert.Equal(ConcurrentDedupIndex.HeldGroup, db.GetDedupGroup(long.Parse(held!))!.IsProvisional);
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
            {
                conn.Open();
                using var count = conn.CreateCommand();
                count.CommandText = "SELECT COUNT(*) FROM dedup_keys WHERE group_id = $g;";
                count.Parameters.AddWithValue("$g", long.Parse(held!));
                Assert.Equal(0L, (long)count.ExecuteScalar()!); // no keys, so nothing can match into it
            }
            Assert.Equal(raptor.DedupGroupId, tagged.DedupGroupId);

            var toPlace = db.GetItemsToPlace();
            Assert.Equal(new[] { "vu" }, toPlace.Select(i => i.Id));
            index.ProcessBatch(toPlace, db);
            Assert.Equal(1, db.DeleteEmptyHeldGroups());

            var placed = db.GetGroupVersions(db.GetGroupIdForDedupKey("tvdb:ep:9057492")!.Value.ToString()).Select(v => v.Id).OrderBy(x => x);
            Assert.Equal(new[] { "r", "vt" }, placed); // as with deferral: refused from the renumbered pair
            Assert.Null(db.GetDedupGroup(long.Parse(held!)));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public void Rebuild_PlacesUntaggedEpisodesLast()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        try
        {
            using var db = new DatabaseService(dbPath);
            var (raptor, untagged, tagged) = UntaggedArrivingFirst();
            foreach (var it in new[] { raptor, untagged, tagged }) db.UpsertItem(it, it.ServerId!); // rebuild reads rowid order

            new DedupEngine().RebuildIndex(db);

            var group = db.GetGroupVersions(db.GetGroupIdForDedupKey("tvdb:ep:9057492")!.Value.ToString()).Select(v => v.Id).OrderBy(x => x).ToList();
            Assert.Equal(new[] { "r", "vt" }, group);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(dbPath); } catch { }
        }
    }

    [Fact]
    public async Task Sync_PlacesHeldBackAndLeftoverUngroupedEpisodes()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new DatabaseService(Path.Combine(dir, "a.db"));
            var (raptor, untagged, tagged) = UntaggedArrivingFirst();
            foreach (var it in new[] { raptor, untagged, tagged }) db.UpsertItem(it, it.ServerId!);
            new ConcurrentDedupIndex().ProcessBatch(new[] { raptor, untagged, tagged }, db, ConcurrentDedupIndex.UntaggedEpisodes.Defer);
            Assert.Single(db.GetItemsWithNullGroupId()); // as an interrupted sync leaves it

            var settings = new SettingsService(Path.Combine(dir, SettingsService.SettingsFileName));
            settings.Update(s => s.Servers.Add(new ServerConfig { Id = "v1", Name = "Valkyrie", Url = "http://127.0.0.1:9", AccessToken = "t", UserId = "u" }));
            await new SyncEngine(new EmbyClient(), db, new DedupEngine(), new TraktService(new TraktAuthService(db)), settings)
                .RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));

            Assert.Empty(db.GetItemsWithNullGroupId());
            Assert.DoesNotContain(db.GetGroupVersions(raptor.DedupGroupId!), v => v.Id == "vu");
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // #20 item 1: servers write "Imdb" and "IMDB"; the exact-key lookup saw only the first (336,776 live items).
    [Fact]
    public void ProviderIds_AreReadRegardlessOfCase()
    {
        var item = new EmbyItem
        {
            ProviderIds = new() { ["IMDB"] = "tt7597088", ["TMDB"] = "936", ["thetvdb"] = "4501235" },
            SeriesProviderIds = new() { ["IMDB"] = "tt0069641" }
        };
        Assert.Equal("tt7597088", item.ImdbId);
        Assert.Equal("936", item.TmdbId);
        Assert.Equal("4501235", item.TvdbId);
        Assert.Equal("tt0069641", item.SeriesImdbId);

        var emptyExact = new EmbyItem { ProviderIds = new() { ["Imdb"] = "", ["IMDB"] = "tt0000001" } };
        Assert.Equal("tt0000001", emptyExact.ImdbId);
    }

    [Fact]
    public void UpperCaseImdbId_MatchesAcrossServers()
    {
        var index = new ConcurrentDedupIndex();
        var raptor = new EmbyItem { Id = "r", ServerId = "raptor", Name = "Pilot", Type = "Episode", SeriesName = "Show A", ParentIndexNumber = 1, IndexNumber = 1, ProviderIds = new() { ["IMDB"] = "tt1234567" } };
        var valkyrie = new EmbyItem { Id = "v", ServerId = "valkyrie", Name = "Pilot", Type = "Episode", SeriesName = "Show B", ParentIndexNumber = 2, IndexNumber = 5, ProviderIds = new() { ["Imdb"] = "tt1234567" } };

        index.ProcessBatch(new[] { raptor, valkyrie });

        Assert.Equal(raptor.DedupGroupId, valkyrie.DedupGroupId); // only the IMDb id links them
    }

    [Fact]
    public void UpperCaseImdbId_IsSeenByTheConflictGuard()
    {
        var index = new ConcurrentDedupIndex();
        var a = new EmbyItem { Id = "a", ServerId = "raptor", Name = "Dune", Type = "Movie", ProviderIds = new() { ["Tmdb"] = "438631", ["IMDB"] = "tt1160419" } };
        var b = new EmbyItem { Id = "b", ServerId = "valkyrie", Name = "Dune", Type = "Movie", ProviderIds = new() { ["Tmdb"] = "438631", ["Imdb"] = "tt0087182" } };

        index.ProcessBatch(new[] { a, b });

        Assert.NotEqual(a.DedupGroupId, b.DedupGroupId);
        Assert.Equal(1, index.ConflictsRefused);
    }
}
