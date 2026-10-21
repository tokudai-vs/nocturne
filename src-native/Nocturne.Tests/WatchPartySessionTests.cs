using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

// Stage F: a real local-only session (HTTP + sync socket on 127.0.0.1, no ffmpeg, no tunnel) driven by
// ClientWebSocket guests.
public class WatchPartySessionTests : IAsyncLifetime
{
    private readonly string _bin = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private WatchPartyService _party = null!;

    public async Task InitializeAsync()
    {
        _party = new WatchPartyService(new WatchPartyBinaryManager(_bin));
        // A party started from a resume point 10 minutes in: the bug case.
        await _party.StartSessionAsync("Resume test", "unused", durationSec: 3600, startOffsetSec: 600, targetHeight: 1080,
            publicLink: false, transcode: false, CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _party.StopSessionAsync();
        try { Directory.Delete(_bin, recursive: true); } catch { }
    }

    private async Task<ClientWebSocket> JoinAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(_party.LocalUrl!.Replace("http://", "ws://") + "/ws"), CancellationToken.None);
        return ws;
    }

    private static async Task<JsonElement> NextAsync(ClientWebSocket ws, string type)
    {
        var buffer = new byte[8192];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException($"closed: {ws.CloseStatus} {ws.CloseStatusDescription}");
            var msg = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count)).RootElement.Clone();
            if (msg.GetProperty("type").GetString() == type) return msg;
        }
    }

    [Fact]
    public async Task ResumeStartedParty_SendsGuestsStreamTime_NotMovieTime()
    {
        using var guest = await JoinAsync();
        var info = await NextAsync(guest, "session_info");
        Assert.False(info.TryGetProperty("startOffsetSec", out _)); // conversion happens at the source now

        _party.MarkShowLive();
        Assert.Equal(0, (await NextAsync(guest, "play")).GetProperty("position").GetDouble(), 3);

        _party.BroadcastHeartbeat(612.5, playing: true); // mpv reports movie time; the 1 s timer sends it
        Assert.Equal(12.5, (await NextAsync(guest, "heartbeat")).GetProperty("position").GetDouble(), 3);

        _party.BroadcastPause(630);
        Assert.Equal(30, (await NextAsync(guest, "pause")).GetProperty("position").GetDouble(), 3);
    }

    [Fact]
    public async Task EleventhGuest_IsRefusedWithSessionFull()
    {
        var guests = new List<ClientWebSocket>();
        for (int i = 0; i < WatchPartyService.MaxGuests; i++)
        {
            var g = await JoinAsync();
            await NextAsync(g, "state");
            guests.Add(g);
        }
        Assert.Equal(10, WatchPartyService.MaxGuests);
        Assert.Equal(WatchPartyService.MaxGuests, _party.GuestCount);

        using var extra = await JoinAsync();
        await NextAsync(extra, "session_full");
        var buffer = new byte[256];
        var close = await extra.ReceiveAsync(buffer, new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
        Assert.Equal(WebSocketMessageType.Close, close.MessageType);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, extra.CloseStatus); // 1008
        Assert.Equal(WatchPartyService.MaxGuests, _party.GuestCount);

        foreach (var g in guests) g.Dispose();
    }

    [Fact]
    public async Task GuestPositionReports_ShowHowFarGuestsTrail()
    {
        using var guest = await JoinAsync();
        await NextAsync(guest, "state");
        _party.MarkShowLive();
        _party.BroadcastHeartbeat(610, playing: true); // stream time 10

        var reported = new TaskCompletionSource<double>();
        _party.GuestsBehindChanged += (_, sec) => reported.TrySetResult(sec);
        await guest.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"position\",\"position\":3}"), WebSocketMessageType.Text, true, CancellationToken.None);

        Assert.Equal(7, await reported.Task.WaitAsync(TimeSpan.FromSeconds(5)), 3);
    }

    [Fact]
    public async Task BurstOfHostEvents_ReachesTheGuestIntact()
    {
        // mpv raises position events several times a second on top of the 1 s heartbeat; every broadcast is
        // fire-and-forget. Overlapping SendAsync calls on one socket were dropped (and degraded the connection).
        using var guest = await JoinAsync();
        await NextAsync(guest, "state");

        Parallel.For(1, 51, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i => _party.BroadcastSeek(600 + i));

        for (int received = 0; received < 50; received++) await NextAsync(guest, "seek"); // concurrent callers may share a position
        Assert.Equal(WebSocketState.Open, guest.State);
    }

    [Fact]
    public async Task Stop_WithGuestsConnected_Completes()
    {
        var guests = new List<ClientWebSocket>();
        for (int i = 0; i < 3; i++) { var g = await JoinAsync(); await NextAsync(g, "state"); guests.Add(g); }

        await _party.StopSessionAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WatchPartySessionState.Idle, _party.State);
        Assert.Equal(0, _party.GuestCount);
        foreach (var g in guests) g.Dispose();
    }

    [Fact]
    public async Task GuestPage_LoadsTheBundledHlsJs_FromTheLocalServer()
    {
        Assert.Contains("<script src=\"/hls.min.js\">", WatchPartyGuestPage.Html);
        Assert.DoesNotContain("cdn.jsdelivr.net", WatchPartyGuestPage.Html);

        using var http = new HttpClient();
        var response = await http.GetAsync(_party.LocalUrl + "/hls.min.js");
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("1.6.16", await response.Content.ReadAsStringAsync());
    }
}

public class WatchPartyBinaryAndJobTests
{
    [Fact]
    public void BinariesThatDoNotMatchTheirPinnedHash_AreNotReady()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var manager = new WatchPartyBinaryManager(dir);
            File.WriteAllText(manager.GetFfmpegPath(), "not ffmpeg");
            File.WriteAllText(manager.GetCloudflaredPath(), "not cloudflared");

            Assert.False(manager.IsReady()); // was File.Exists
            Assert.Throws<InvalidDataException>(() =>
                WatchPartyBinaryManager.Verify(manager.GetFfmpegPath(), WatchPartyBinaryManager.FfmpegExeSha256, "ffmpeg"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ClosingTheJob_KillsItsProcesses()
    {
        using var job = ChildProcessJob.Create();
        Assert.NotNull(job);
        using var child = Process.Start(new ProcessStartInfo("ping", "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        Assert.True(ChildProcessJob.Assign(job!, child));

        job!.Dispose(); // what Windows does to the app's job handle when Nocturne exits or crashes

        Assert.True(child.WaitForExit(5000), "a process in the job outlived the job handle");
    }
}
