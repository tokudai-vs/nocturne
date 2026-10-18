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
}
