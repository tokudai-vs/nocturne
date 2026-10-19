using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class EmbyConnectServiceTests
{
    [Fact]
    public void PinCreationResult_DeserializesCorrectly()
    {
        var json = "{\"Id\":\"23299044\",\"Pin\":\"80744\",\"DeviceId\":\"test-device-12345\",\"IsExpired\":false,\"IsConfirmed\":false,\"AccessToken\":\"none\"}";
        var result = JsonSerializer.Deserialize<PinCreationResult>(json);

        Assert.NotNull(result);
        Assert.Equal("23299044", result.Id);
        Assert.Equal("80744", result.Pin);
        Assert.Equal("test-device-12345", result.DeviceId);
        Assert.False(result.IsExpired);
        Assert.False(result.IsConfirmed);
    }

    [Fact]
    public void PinExchangeResult_DeserializesCorrectly()
    {
        var json = "{\"AccessToken\":\"connect_token_abc123\",\"UserId\":\"user_guid_789\"}";
        var result = JsonSerializer.Deserialize<PinExchangeResult>(json);

        Assert.NotNull(result);
        Assert.Equal("connect_token_abc123", result.AccessToken);
        Assert.Equal("user_guid_789", result.UserId);
    }

    [Fact]
    public void ConnectServer_DeserializesArrayCorrectly()
    {
        var json = "[{\"AccessKey\":\"key_123\",\"SystemId\":\"sys_456\",\"Name\":\"Home Emby\",\"Url\":\"https://emby.remote.org\",\"LocalAddress\":\"http://192.168.1.100:8096\"}]";
        var servers = JsonSerializer.Deserialize<List<ConnectServer>>(json);

        Assert.NotNull(servers);
        Assert.Single(servers);
        Assert.Equal("key_123", servers[0].AccessKey);
        Assert.Equal("Home Emby", servers[0].Name);
        Assert.Equal("http://192.168.1.100:8096", servers[0].LocalAddress);
    }

    [Fact]
    public void ConnectExchangeResponse_DeserializesCorrectly()
    {
        var json = "{\"LocalUserId\":\"local_user_999\",\"AccessToken\":\"server_session_token_xyz\"}";
        var result = JsonSerializer.Deserialize<ConnectExchangeResponse>(json);

        Assert.NotNull(result);
        Assert.Equal("local_user_999", result.LocalUserId);
        Assert.Equal("server_session_token_xyz", result.AccessToken);
    }

    [Fact]
    public void EmbyConnectService_InitializesWithPersistentDeviceId()
    {
        var service = new EmbyConnectService();
        Assert.False(string.IsNullOrWhiteSpace(service.DeviceId));
        Assert.Equal(service.DeviceId, EmbyClient.GetDeviceId());
    }
}
