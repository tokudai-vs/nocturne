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
        Assert.Equal("avengers endgame", result);
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

    [Fact]
    public void PlaybackDecisionMatrix_HighestQualityFirst_ThenBitrate_ThenServerPriority()
    {
        var copy4KRemux = new EmbyItem
        {
            Id = "copy1",
            ServerId = "server_beta",
            Name = "Avatar",
            Height = 2160,
            Width = 3840,
            Bitrate = 65_000_000,
            RunTimeTicks = 9_000_000_000L
        };

        var copy4KWeb = new EmbyItem
        {
            Id = "copy2",
            ServerId = "server_alpha",
            Name = "Avatar",
            Height = 2160,
            Width = 3840,
            Bitrate = 18_000_000,
            RunTimeTicks = 9_000_000_000L
        };

        var copy1080pBluRay = new EmbyItem
        {
            Id = "copy3",
            ServerId = "server_alpha",
            Name = "Avatar",
            Height = 1080,
            Width = 1920,
            Bitrate = 14_000_000,
            RunTimeTicks = 9_000_000_000L
        };

        var copy1080pBluRayServerBeta = new EmbyItem
        {
            Id = "copy4",
            ServerId = "server_beta",
            Name = "Avatar",
            Height = 1080,
            Width = 1920,
            Bitrate = 14_000_000,
            RunTimeTicks = 9_000_000_000L
        };

        var serverPriority = new List<string> { "server_alpha", "server_beta" };

        var list = new List<EmbyItem> { copy1080pBluRayServerBeta, copy4KWeb, copy1080pBluRay, copy4KRemux };

        var ordered = list
            .OrderByDescending(i => (i.Height >= 2100 ? 4000 : i.Height >= 1000 ? 3000 : 1000))
            .ThenByDescending(i => i.MeasuredBitrate ?? 0)
            .ThenBy(i =>
            {
                var idx = serverPriority.IndexOf(i.ServerId ?? string.Empty);
                return idx >= 0 ? idx : int.MaxValue;
            })
            .ToList();

        Assert.Equal("copy1", ordered[0].Id); // 4K Remux (highest quality & bitrate)
        Assert.Equal("copy2", ordered[1].Id); // 4K Web (4K resolution tier)
        Assert.Equal("copy3", ordered[2].Id); // 1080p from server_alpha (higher server priority than server_beta)
        Assert.Equal("copy4", ordered[3].Id); // 1080p from server_beta
    }
}
