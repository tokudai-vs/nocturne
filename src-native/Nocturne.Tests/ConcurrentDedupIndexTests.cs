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
        Assert.Null(batch1[0].DedupGroupId); // Single item so far
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
        Assert.Null(item1.DedupGroupId);
        Assert.Null(item2.DedupGroupId);
    }

    [Fact]
    public void ProcessBatch_EpisodesAndNonVideoTypesIgnored()
    {
        var index = new ConcurrentDedupIndex();

        var ep1 = new EmbyItem { Id = "ep1", Name = "Pilot", Type = "Episode", ProductionYear = 2008 };
        var ep2 = new EmbyItem { Id = "ep2", Name = "Pilot", Type = "Episode", ProductionYear = 2008 };

        index.ProcessBatch(new[] { ep1, ep2 });
        Assert.Equal(0, index.MultiVersionGroupsCount);
        Assert.Null(ep1.DedupGroupId);
        Assert.Null(ep2.DedupGroupId);
    }
}
