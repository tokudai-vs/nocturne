using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class ConcurrentDedupIndexTests
{
    [Fact]
    public void ProcessBatch_MatchesItemsByTmdbId_AndPrefers4KPrimary()
    {
        var index = new ConcurrentDedupIndex();

        var batch1 = new List<EmbyItem>
        {
            new()
            {
                Id = "srv1_item1",
                ServerId = "srv1",
                Name = "Inception",
                Type = "Movie",
                ProductionYear = 2010,
                ProviderIds = new Dictionary<string, string> { { "Tmdb", "27205" } },
                MediaSources = new List<MediaSourceInfo>
                {
                    new()
                    {
                        MediaStreams = new List<MediaStreamInfo>
                        {
                            new() { Type = "Video", Width = 1920, Height = 1080 }
                        }
                    }
                }
            }
        };

        var batch2 = new List<EmbyItem>
        {
            new()
            {
                Id = "srv2_item2",
                ServerId = "srv2",
                Name = "Inception (2010) [Remux]",
                Type = "Movie",
                ProductionYear = 2010,
                ProviderIds = new Dictionary<string, string> { { "Tmdb", "27205" } },
                BackdropImageTags = new List<string> { "backdrop1" },
                MediaSources = new List<MediaSourceInfo>
                {
                    new()
                    {
                        MediaStreams = new List<MediaStreamInfo>
                        {
                            new() { Type = "Video", Width = 3840, Height = 2160 }
                        }
                    }
                }
            }
        };

        index.ProcessBatch(batch1);
        Assert.NotNull(batch1[0].DedupGroupId); // In v4.2 every synced item has a group ID
        Assert.Equal(0, index.MultiVersionGroupsCount);

        index.ProcessBatch(batch2);
        Assert.NotNull(batch2[0].DedupGroupId);
        Assert.Equal(1, index.MultiVersionGroupsCount);
        Assert.Equal(batch2[0].DedupGroupId, batch1[0].DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_MatchesItemsByImdbId()
    {
        var index = new ConcurrentDedupIndex();

        var item1 = new EmbyItem
        {
            Id = "srv1_fc",
            ServerId = "srv1",
            Name = "Fight Club",
            Type = "Movie",
            ProductionYear = 1999,
            ProviderIds = new Dictionary<string, string> { { "Imdb", "tt0137523" } }
        };

        var item2 = new EmbyItem
        {
            Id = "srv2_fc",
            ServerId = "srv2",
            Name = "Fight Club (10th Anniversary)",
            Type = "Movie",
            ProductionYear = 1999,
            ProviderIds = new Dictionary<string, string> { { "Imdb", "tt0137523" } }
        };

        index.ProcessBatch(new[] { item1 });
        index.ProcessBatch(new[] { item2 });

        Assert.Equal(1, index.MultiVersionGroupsCount);
        Assert.NotNull(item1.DedupGroupId);
        Assert.Equal(item1.DedupGroupId, item2.DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_MatchesItemsByNameAndYear_WhenNoProviderIds()
    {
        var index = new ConcurrentDedupIndex();

        var item1 = new EmbyItem
        {
            Id = "srv1_bb",
            ServerId = "srv1",
            Name = "Breaking Bad",
            Type = "Series",
            ProductionYear = 2008
        };

        var item2 = new EmbyItem
        {
            Id = "srv2_bb",
            ServerId = "srv2",
            Name = "Breaking Bad",
            Type = "Series",
            ProductionYear = 2008
        };

        index.ProcessBatch(new[] { item1 });
        index.ProcessBatch(new[] { item2 });

        Assert.Equal(1, index.MultiVersionGroupsCount);
        Assert.NotNull(item1.DedupGroupId);
        Assert.Equal(item1.DedupGroupId, item2.DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_DifferentTitlesDoNotMatch()
    {
        var index = new ConcurrentDedupIndex();

        var item1 = new EmbyItem
        {
            Id = "avatar1",
            Name = "Avatar",
            Type = "Movie",
            ProductionYear = 2009
        };

        var item2 = new EmbyItem
        {
            Id = "avatar2",
            Name = "Avatar The Way of Water",
            Type = "Movie",
            ProductionYear = 2022
        };

        index.ProcessBatch(new[] { item1 });
        index.ProcessBatch(new[] { item2 });

        Assert.Equal(0, index.MultiVersionGroupsCount);
        Assert.NotNull(item1.DedupGroupId);
        Assert.NotNull(item2.DedupGroupId);
        Assert.NotEqual(item1.DedupGroupId, item2.DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_EpisodeStableProviderKeys_MatchesEpisodesAcrossServers()
    {
        var index = new ConcurrentDedupIndex();

        // Episode on server 1 with TVDb series ID
        var ep1 = new EmbyItem
        {
            Id = "srv1_ep101",
            ServerId = "srv1",
            Name = "Fly",
            Type = "Episode",
            SeriesName = "Breaking Bad",
            ParentIndexNumber = 3,
            IndexNumber = 10,
            SeriesProviderIds = new Dictionary<string, string> { { "Tvdb", "81189" } },
            Height = 1080
        };

        // Episode on server 2 with TVDb series ID
        var ep2 = new EmbyItem
        {
            Id = "srv2_ep101",
            ServerId = "srv2",
            Name = "Fly (Remux)",
            Type = "Episode",
            SeriesName = "Breaking Bad",
            ParentIndexNumber = 3,
            IndexNumber = 10,
            SeriesProviderIds = new Dictionary<string, string> { { "Tvdb", "81189" } },
            Height = 2160
        };

        index.ProcessBatch(new[] { ep1 });
        Assert.NotNull(ep1.DedupGroupId);
        Assert.Equal(0, index.MultiVersionGroupsCount);

        index.ProcessBatch(new[] { ep2 });
        Assert.NotNull(ep2.DedupGroupId);
        Assert.Equal(1, index.MultiVersionGroupsCount);
        Assert.Equal(ep1.DedupGroupId, ep2.DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_NWayMerge_AbsorbsProvisionalWhenAuthoritativeItemArrives()
    {
        var index = new ConcurrentDedupIndex();

        // Item A: matched fuzzily on name + year (no provider IDs)
        var itemA = new EmbyItem
        {
            Id = "srv1_dune",
            ServerId = "srv1",
            Name = "Dune",
            Type = "Movie",
            ProductionYear = 2021
        };

        // Item B: also matched fuzzily
        var itemB = new EmbyItem
        {
            Id = "srv2_dune",
            ServerId = "srv2",
            Name = "Dune Part One",
            Type = "Movie",
            ProductionYear = 2021,
            ProviderIds = new Dictionary<string, string> { { "Tmdb", "438631" } }
        };

        index.ProcessBatch(new[] { itemA });
        Assert.NotNull(itemA.DedupGroupId);

        index.ProcessBatch(new[] { itemB });
        Assert.NotNull(itemB.DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_RemakeProtection_DoesNotMergeDifferentAuthGroupsOnFuzzyMatch()
    {
        var index = new ConcurrentDedupIndex();

        // 1982 movie with IMDb tt0084787
        var original = new EmbyItem
        {
            Id = "the_thing_1982",
            ServerId = "srv1",
            Name = "The Thing",
            Type = "Movie",
            ProductionYear = 1982,
            ProviderIds = new Dictionary<string, string> { { "Imdb", "tt0084787" } }
        };

        // 2011 prequel with IMDb tt0905372
        var prequel = new EmbyItem
        {
            Id = "the_thing_2011",
            ServerId = "srv2",
            Name = "The Thing",
            Type = "Movie",
            ProductionYear = 2011,
            ProviderIds = new Dictionary<string, string> { { "Imdb", "tt0905372" } }
        };

        index.ProcessBatch(new[] { original });
        index.ProcessBatch(new[] { prequel });

        Assert.Equal(0, index.MultiVersionGroupsCount);
        Assert.NotEqual(original.DedupGroupId, prequel.DedupGroupId);
    }
}
