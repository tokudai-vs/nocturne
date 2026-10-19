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
}
