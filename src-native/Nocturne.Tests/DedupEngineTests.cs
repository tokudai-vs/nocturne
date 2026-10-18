using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class DedupEngineTests
{
    private readonly DedupEngine _engine = new();

    [Fact]
    public void NormalizeTitle_CleansPunctuationAndWhitespace()
    {
        var result = DedupEngine.NormalizeTitle("Avengers: Endgame (2019) - IMAX [Edition]!");
        Assert.Equal("avengers endgame 2019 imax edition", result);
    }

    [Fact]
    public void Deduplicate_MatchesMoviesByTmdbId()
    {
        var items = new List<EmbyItem>
        {
            new()
            {
                Id = "srv1_item1",
                Name = "Inception",
                Type = "Movie",
                ProductionYear = 2010,
                ProviderIds = new Dictionary<string, string> { { "Tmdb", "27205" } }
            },
            new()
            {
                Id = "srv2_item2",
                Name = "Inception (2010) [4K Remux]",
                Type = "Movie",
                ProductionYear = 2010,
                ProviderIds = new Dictionary<string, string> { { "Tmdb", "27205" } }
            }
        };

        var groups = _engine.Deduplicate(items);

        Assert.Single(groups);
        var group = groups[0];
        Assert.Equal(2, group.VersionCount);
        Assert.Equal("27205", group.TmdbId);
    }

    [Fact]
    public void Deduplicate_MatchesMoviesByNameAndYearWhenNoIds()
    {
        var items = new List<EmbyItem>
        {
            new()
            {
                Id = "itemA",
                Name = "The Dark Knight",
                Type = "Movie",
                ProductionYear = 2008
            },
            new()
            {
                Id = "itemB",
                Name = "The Dark Knight: Special Edition",
                Type = "Movie",
                ProductionYear = 2008
            }
        };

        var groups = _engine.Deduplicate(items);

        Assert.Equal(2, groups.Count); // Different names don't clash
    }

    [Fact]
    public void ScorePrimaryItem_Prefers4KOver1080p()
    {
        var copy1080p = new EmbyItem
        {
            Id = "1080p",
            Name = "Interstellar",
            Type = "Movie",
            MediaSources = new List<MediaSourceInfo>
            {
                new()
                {
                    MediaStreams = new List<MediaStreamInfo>
                    {
                        new() { Type = "Video", Width = 1920, Height = 1080, Codec = "h264" }
                    }
                }
            }
        };

        var copy4K = new EmbyItem
        {
            Id = "4k",
            Name = "Interstellar",
            Type = "Movie",
            BackdropImageTags = new List<string> { "backdrop1" },
            ImageTags = new Dictionary<string, string> { { "Primary", "tag1" } },
            Overview = "A team of explorers travel through a wormhole in space.",
            CommunityRating = 8.7f,
            MediaSources = new List<MediaSourceInfo>
            {
                new()
                {
                    MediaStreams = new List<MediaStreamInfo>
                    {
                        new() { Type = "Video", Width = 3840, Height = 2160, Codec = "hevc" }
                    }
                }
            }
        };

        var score1080 = DedupEngine.ScorePrimaryItem(copy1080p);
        var score4K = DedupEngine.ScorePrimaryItem(copy4K);

        Assert.True(score4K > score1080);
    }
}
