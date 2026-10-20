using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

public class WatchPartyServiceTests
{
    [Fact]
    public void BinaryManager_Initialization_HasValidPaths()
    {
        var manager = new WatchPartyBinaryManager();
        Assert.False(string.IsNullOrEmpty(manager.BinDir));
        Assert.EndsWith("ffmpeg.exe", manager.GetFfmpegPath());
        Assert.EndsWith("cloudflared.exe", manager.GetCloudflaredPath());
    }

    [Fact]
    public void WatchPartyService_InitialState_IsIdle()
    {
        var manager = new WatchPartyBinaryManager();
        using var service = new WatchPartyService(manager);

        Assert.Equal(WatchPartySessionState.Idle, service.State);
        Assert.False(service.IsActive);
        Assert.Null(service.TunnelUrl);
        Assert.Null(service.SessionId);
        Assert.Equal(0, service.GuestCount);
    }

    [Fact]
    public async Task WatchPartyService_StopSessionAsync_WhenIdle_DoesNotThrow()
    {
        var manager = new WatchPartyBinaryManager();
        using var service = new WatchPartyService(manager);

        await service.StopSessionAsync();
        Assert.Equal(WatchPartySessionState.Idle, service.State);
    }
}
