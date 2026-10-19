using System.Text.Json;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class EmbyClientTests
{
    [Theory]
    [InlineData("192.168.1.50:8096", "http://192.168.1.50:8096")]
    [InlineData("http://192.168.1.50:8096/", "http://192.168.1.50:8096")]
    [InlineData("https://emby.myhome.org/", "https://emby.myhome.org")]
    [InlineData("  http://emby.local:8096  ", "http://emby.local:8096")]
    public void NormalizeServerUrl_FormatsCorrectly(string input, string expected)
    {
        var actual = EmbyClient.NormalizeServerUrl(input);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void AuthenticatePayload_SerializesToExactPascalCase()
    {
        var payload = new AuthenticatePayload
        {
            Username = "Alice",
            Pw = "SecretPassword123"
        };

        var json = JsonSerializer.Serialize(payload);

        // Assert exact PascalCase properties expected by Emby ServiceStack
        Assert.Contains("\"Username\":\"Alice\"", json);
        Assert.Contains("\"Pw\":\"SecretPassword123\"", json);
        Assert.DoesNotContain("\"username\"", json);
        Assert.DoesNotContain("\"pw\"", json);
    }

    [Fact]
    public void Configure_SetsUrlAndToken_PreservesIsAuthenticated()
    {
        var client = new EmbyClient();
        Assert.False(client.IsAuthenticated);

        client.Configure("http://emby.local:8096", "token_abc123", "user_xyz789");

        Assert.True(client.IsAuthenticated);
        Assert.Equal("token_abc123", client.Token);
        Assert.Equal("user_xyz789", client.UserId);
        Assert.Equal("http://emby.local:8096", client.BaseUrl);
    }

    [Fact]
    public async Task GetServerInfoAsync_DoesNotWipeExistingTokenOrUserId()
    {
        var client = new EmbyClient();
        client.Configure("http://emby.local:8096", "token_abc123", "user_xyz789");

        // Attempting to query server info (even for an unreachable address) must never reset active token or userId
        var result = await client.GetServerInfoAsync("http://192.0.2.1:8096");

        Assert.True(client.IsAuthenticated);
        Assert.Equal("token_abc123", client.Token);
        Assert.Equal("user_xyz789", client.UserId);
        Assert.Equal("http://emby.local:8096", client.BaseUrl);
    }

    [Fact]
    public async Task GetPublicUsersAsync_DoesNotWipeExistingTokenOrUserId()
    {
        var client = new EmbyClient();
        client.Configure("http://emby.local:8096", "token_abc123", "user_xyz789");

        // Querying public users must never reset active token or userId
        var result = await client.GetPublicUsersAsync("http://192.0.2.1:8096");

        Assert.True(client.IsAuthenticated);
        Assert.Equal("token_abc123", client.Token);
        Assert.Equal("user_xyz789", client.UserId);
    }

    [Fact]
    public void EmbyItem_DeserializesWithObjectStudiosAndGenres()
    {
        var json = """
        {
            "Id": "1234",
            "Name": "Ozark",
            "Studios": [
                { "Name": "Media Rights Capital", "Id": 101 },
                { "Name": "Aggregate Films", "Id": 102 }
            ],
            "Genres": ["Crime", "Drama"]
        }
        """;

        var item = JsonSerializer.Deserialize<Nocturne.Core.Models.EmbyItem>(json);
        Assert.NotNull(item);
        Assert.Equal("1234", item.Id);
        Assert.Equal("Ozark", item.Name);
        Assert.NotNull(item.Studios);
        Assert.Equal(2, item.Studios.Count);
        Assert.Equal("Media Rights Capital", item.Studios[0]);
        Assert.Equal("Aggregate Films", item.Studios[1]);
        Assert.NotNull(item.Genres);
        Assert.Equal(2, item.Genres.Count);
    }

    [Fact]
    public void EmbyItem_DeserializesWithStringStudiosAndNumericIds()
    {
        var json = """
        {
            "Id": 5678,
            "Name": "Bad Apples",
            "Studios": ["Universal Pictures", "Sony"],
            "People": [
                { "Name": "Actor 1", "Id": 9999 }
            ]
        }
        """;

        var item = JsonSerializer.Deserialize<Nocturne.Core.Models.EmbyItem>(json);
        Assert.NotNull(item);
        Assert.Equal("5678", item.Id);
        Assert.Equal("Bad Apples", item.Name);
        Assert.NotNull(item.Studios);
        Assert.Equal(2, item.Studios.Count);
        Assert.Equal("Universal Pictures", item.Studios[0]);
        Assert.NotNull(item.People);
        Assert.Single(item.People);
        Assert.Equal("9999", item.People[0].Id);
    }

    [Fact]
    public void EmbyItem_DisplayTitle_FormatsEpisodeProperly()
    {
        var ep = new Nocturne.Core.Models.EmbyItem
        {
            Type = "Episode",
            ParentIndexNumber = 1,
            IndexNumber = 4,
            Name = "Tonight's The Night"
        };
        Assert.Equal("S01E04 - Tonight's The Night", ep.DisplayTitle);

        var movie = new Nocturne.Core.Models.EmbyItem
        {
            Type = "Movie",
            Name = "Inception"
        };
        Assert.Equal("Inception", movie.DisplayTitle);
    }

    [Fact]
    public void EmbyItem_ResolutionBadge_Detects4KAnd1080p()
    {
        var item4K = new Nocturne.Core.Models.EmbyItem
        {
            MediaSources = new List<Nocturne.Core.Models.MediaSourceInfo>
            {
                new()
                {
                    MediaStreams = new List<Nocturne.Core.Models.MediaStreamInfo>
                    {
                        new() { Type = "Video", Width = 3840, Height = 2160, Codec = "hevc" }
                    }
                }
            }
        };
        Assert.Equal("4K UHD", item4K.ResolutionBadge);
        Assert.Equal("HEVC", item4K.CodecBadge);

        var item1080p = new Nocturne.Core.Models.EmbyItem
        {
            MediaSources = new List<Nocturne.Core.Models.MediaSourceInfo>
            {
                new()
                {
                    MediaStreams = new List<Nocturne.Core.Models.MediaStreamInfo>
                    {
                        new() { Type = "Video", Width = 1920, Height = 1080, Codec = "h264" }
                    }
                }
            }
        };
        Assert.Equal("1080p", item1080p.ResolutionBadge);
        Assert.Equal("H264", item1080p.CodecBadge);
    }
}
