using System.IO;
using System.Net;
using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// Stop paths and what each one writes locally and reports to the server. MainViewModel maps its entry points
// onto PlaybackStopReason: Stop button, Esc and window close -> Stop; '<' / overlay Previous -> Previous;
// '>' / overlay Next -> Next; mpv end of file -> EndOfFile (any other mpv end reason -> Stop).
public class PlaybackSessionTests : IDisposable
{
    private const long TicksPerSecond = 10_000_000;
    private const long Hour = 3600 * TicksPerSecond;

    private readonly string _tempDbPath;
    private readonly DatabaseService _db;
    private readonly RecordingHandler _http = new();
    private readonly EmbyClient _client;
    private DateTime _now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    public PlaybackSessionTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseService(_tempDbPath);
        _client = new EmbyClient(_http);
        _client.Configure("http://valkyrie.test", "token", "user1");
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_tempDbPath); } catch { }
    }

    private PlaybackSession NewSession(bool cascade = false) => new(_db, cascade, () => _now);

    private static EmbyItem Movie(string id = "m1", string server = "valkyrie", long? runtime = Hour) =>
        new() { Id = id, ServerId = server, Name = "Movie " + id, Type = "Movie", RunTimeTicks = runtime };

    private long? StoppedTicks() => _http.Bodies("/Sessions/Playing/Stopped").Select(b => (long?)b.GetProperty("PositionTicks").GetInt64()).SingleOrDefault();
    private bool MarkedPlayed(string id) => _http.Requests.Any(r => r.Method == "POST" && r.Path.EndsWith($"/PlayedItems/{id}"));

    [Theory]
    [InlineData(PlaybackStopReason.Stop)]      // Stop button, Esc, window close
    [InlineData(PlaybackStopReason.Previous)]
    [InlineData(PlaybackStopReason.Next)]
    public async Task MidPlaybackStop_ReportsRealPosition_AndWritesLocalResume(PlaybackStopReason reason)
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);
        session.OnPosition(600);

        var result = await session.FinalizeAsync(reason);

        Assert.NotNull(result);
        Assert.False(result!.Completed);
        Assert.Equal(600 * TicksPerSecond, StoppedTicks());
        Assert.False(MarkedPlayed("m1"));
        var local = _db.GetUserData("valkyrie", "m1");
        Assert.NotNull(local);
        Assert.Equal(600 * TicksPerSecond, local!.PlaybackPositionTicks);
        Assert.False(local.Played);
    }

    [Fact]
    public async Task StopBeforeFirstPositionEvent_ReportsTheResumePointNotZero()
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 2400 * TicksPerSecond);

        await session.FinalizeAsync(PlaybackStopReason.Stop);

        Assert.Equal(2400 * TicksPerSecond, StoppedTicks());
        Assert.Equal(2400 * TicksPerSecond, _db.GetUserData("valkyrie", "m1")!.PlaybackPositionTicks);
    }

    [Fact]
    public async Task StopInFirstSeconds_ReportsPositionButWritesNoLocalResume()
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);
        session.OnPosition(3);

        await session.FinalizeAsync(PlaybackStopReason.Stop);

        Assert.Equal(3 * TicksPerSecond, StoppedTicks());
        Assert.Null(_db.GetUserData("valkyrie", "m1"));
    }

    [Theory]
    [InlineData(PlaybackStopReason.Stop)]
    [InlineData(PlaybackStopReason.Next)]
    [InlineData(PlaybackStopReason.EndOfFile)]
    public async Task PastCompletionThreshold_MarksPlayedOnOwningServerAndLocally(PlaybackStopReason reason)
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);
        session.OnPosition(3500); // 97%

        var result = await session.FinalizeAsync(reason);

        Assert.True(result!.Completed);
        Assert.True(MarkedPlayed("m1"));
        Assert.Equal(3500 * TicksPerSecond, StoppedTicks());
        var local = _db.GetUserData("valkyrie", "m1");
        Assert.True(local!.Played);
        Assert.Equal(0, local.PlaybackPositionTicks);
    }

    [Fact]
    public async Task PrematureEndOfFile_IsNotACompletion()
    {
        // A stream that dies at 40% can surface as an end of file; it must not be marked watched.
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);
        session.OnPosition(1440);

        Assert.False(session.WouldComplete(PlaybackStopReason.EndOfFile));
        var result = await session.FinalizeAsync(PlaybackStopReason.EndOfFile);

        Assert.False(result!.Completed);
        Assert.False(MarkedPlayed("m1"));
        Assert.Equal(1440 * TicksPerSecond, _db.GetUserData("valkyrie", "m1")!.PlaybackPositionTicks);
    }

    [Fact]
    public async Task EndOfFile_UsesPlayerDurationWhenRuntimeIsUnknown()
    {
        var session = NewSession();
        session.Begin(Movie(runtime: null), _client, "ms1", 0);
        session.OnDuration(1000);
        session.OnPosition(999);

        Assert.True(session.WouldComplete(PlaybackStopReason.EndOfFile));
        Assert.True((await session.FinalizeAsync(PlaybackStopReason.EndOfFile))!.Completed);
    }

    [Fact]
    public async Task EndOfFile_WithNoKnownLength_CountsAsCompletion_ButStopDoesNot()
    {
        var eof = NewSession();
        eof.Begin(Movie("a", runtime: null), _client, "ms1", 0);
        eof.OnPosition(1200);
        Assert.True((await eof.FinalizeAsync(PlaybackStopReason.EndOfFile))!.Completed);

        var stop = NewSession();
        stop.Begin(Movie("b", runtime: null), _client, "ms1", 0);
        stop.OnPosition(1200);
        Assert.False((await stop.FinalizeAsync(PlaybackStopReason.Stop))!.Completed);
    }

    [Fact]
    public async Task FinalizeWithoutBegin_WritesNothing()
    {
        // Esc while the player is still starting: nothing has played yet.
        var result = await NewSession().FinalizeAsync(PlaybackStopReason.Stop);

        Assert.Null(result);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task FinalizeTwice_ReportsOnce()
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);
        session.OnPosition(600);

        await session.FinalizeAsync(PlaybackStopReason.Stop);
        await session.FinalizeAsync(PlaybackStopReason.Stop);

        Assert.Single(_http.Bodies("/Sessions/Playing/Stopped"));
    }

    private (EmbyItem A, EmbyItem B) GroupedAcrossServers()
    {
        var a = new EmbyItem { Id = "dune_a", ServerId = "valkyrie", Name = "Dune", Type = "Movie", RunTimeTicks = Hour, ProviderIds = new() { { "Tmdb", "693134" } } };
        var b = new EmbyItem { Id = "dune_b", ServerId = "raptor", Name = "Dune", Type = "Movie", RunTimeTicks = Hour, ProviderIds = new() { { "Tmdb", "693134" } } };
        _db.UpsertItem(a, "valkyrie");
        _db.UpsertItem(b, "raptor");
        var index = new ConcurrentDedupIndex();
        index.SetConnectedServerCount(2);
        index.ProcessBatch(new List<EmbyItem> { a, b }, _db);
        Assert.Equal(a.DedupGroupId, b.DedupGroupId);
        _db.UpdateLocalPlaybackProgress("raptor", "dune_b", 900 * TicksPerSecond);
        return (a, b);
    }

    [Fact]
    public async Task Completion_WithCascadeOff_TouchesOnlyThePlayedVersion()
    {
        var (a, _) = GroupedAcrossServers();
        var session = NewSession(cascade: false);
        session.Begin(a, _client, "ms1", 0);
        session.OnPosition(3590);

        await session.FinalizeAsync(PlaybackStopReason.EndOfFile);

        Assert.True(_db.GetUserData("valkyrie", "dune_a")!.Played);
        var sibling = _db.GetUserData("raptor", "dune_b")!;
        Assert.False(sibling.Played);
        Assert.Equal(900 * TicksPerSecond, sibling.PlaybackPositionTicks);
        Assert.Empty(_db.GetPendingSiblingWrites(10));
    }

    [Fact]
    public async Task Completion_WithCascadeOn_WritesSiblingsLocallyAndQueuesTheOtherServer()
    {
        var (a, _) = GroupedAcrossServers();
        var session = NewSession(cascade: true);
        session.Begin(a, _client, "ms1", 0);
        session.OnPosition(3590);

        await session.FinalizeAsync(PlaybackStopReason.EndOfFile);

        Assert.True(_db.GetUserData("raptor", "dune_b")!.Played);
        var queued = Assert.Single(_db.GetPendingSiblingWrites(10));
        Assert.Equal(("raptor", "dune_b", true), (queued.ServerId, queued.ItemId, queued.MarkPlayed));
    }

    [Fact]
    public async Task DefaultSession_CascadesOnlyOnGroupsBuiltByTheGuardedEngine()
    {
        var (a, _) = GroupedAcrossServers();

        var legacy = new PlaybackSession(_db); // groups present, no engine marker: built by the earlier engine
        legacy.Begin(a, _client, "ms1", 0);
        legacy.OnPosition(3590);
        await legacy.FinalizeAsync(PlaybackStopReason.EndOfFile);
        Assert.False(_db.GetUserData("raptor", "dune_b")!.Played);
        Assert.Empty(_db.GetPendingSiblingWrites(10));

        _db.SetMigrationState(ConcurrentDedupIndex.EngineMarkerKey, ConcurrentDedupIndex.EngineVersion);
        FeatureFlags.MeasureCascadeGate(_db);
        var guarded = new PlaybackSession(_db);
        guarded.Begin(a, _client, "ms1", 0);
        guarded.OnPosition(3590);
        await guarded.FinalizeAsync(PlaybackStopReason.EndOfFile);
        Assert.True(_db.GetUserData("raptor", "dune_b")!.Played);
        Assert.Single(_db.GetPendingSiblingWrites(10));
    }

    [Fact]
    public async Task SiblingDrain_KeepsFailedWrites_AndRemovesAcceptedOnes()
    {
        _db.EnqueueSiblingPlaybackWrite("raptor", "r1", 0, markPlayed: true);
        _db.EnqueueSiblingPlaybackWrite("other", "o1", 0, markPlayed: true);
        var failing = new RecordingHandler { Status = HttpStatusCode.InternalServerError };
        var raptor = new EmbyClient(failing);
        raptor.Configure("http://raptor.test", "token", "user2");

        await NewSession().DrainSiblingWritesAsync(sid => sid == "raptor" ? raptor : _client);

        var left = Assert.Single(_db.GetPendingSiblingWrites(10));
        Assert.Equal("r1", left.ItemId);
        Assert.True(MarkedPlayed("o1"));

        // A row that keeps failing stops blocking the queue but is not deleted.
        for (var i = 1; i < DatabaseService.MaxSiblingWriteAttempts; i++)
            await NewSession().DrainSiblingWritesAsync(_ => raptor);
        Assert.Empty(_db.GetPendingSiblingWrites(10));
    }

    // Item 4 (#19): a sibling server is not streaming, so its resume position goes through the user-data endpoint;
    // a Sessions/Playing report there would open a session nothing ends.
    [Fact]
    public async Task SiblingPositionWrite_UsesUserData_AndKeepsTheServersOtherFields()
    {
        _db.EnqueueSiblingPlaybackWrite("raptor", "r1", 900 * TicksPerSecond, markPlayed: false);
        var http = new RecordingHandler
        {
            GetJson = path => path.EndsWith("/Users/user2/Items/r1")
                ? "{\"Id\":\"r1\",\"UserData\":{\"PlaybackPositionTicks\":0,\"PlayCount\":2,\"IsFavorite\":true,\"Played\":true,\"LastPlayedDate\":\"2026-08-08T17:49:49.0000000Z\"}}"
                : null
        };
        var raptor = new EmbyClient(http);
        raptor.Configure("http://raptor.test", "token", "user2");

        await NewSession().DrainSiblingWritesAsync(_ => raptor);

        Assert.DoesNotContain(http.Requests, r => r.Path.Contains("/Sessions/Playing"));
        var sent = Assert.Single(http.Bodies("/Users/user2/Items/r1/UserData"));
        Assert.Equal(900 * TicksPerSecond, sent.GetProperty("PlaybackPositionTicks").GetInt64());
        Assert.True(sent.GetProperty("Played").GetBoolean());       // Emby resets fields left out of the body
        Assert.True(sent.GetProperty("IsFavorite").GetBoolean());
        Assert.Equal(2, sent.GetProperty("PlayCount").GetInt32());
        Assert.Empty(_db.GetPendingSiblingWrites(10));
    }

    [Fact]
    public async Task SiblingPositionWrite_StaysQueued_WhenTheItemCannotBeRead()
    {
        _db.EnqueueSiblingPlaybackWrite("raptor", "r1", 900 * TicksPerSecond, markPlayed: false);
        var raptor = new EmbyClient(new RecordingHandler { Status = HttpStatusCode.NotFound });
        raptor.Configure("http://raptor.test", "token", "user2");

        await NewSession().DrainSiblingWritesAsync(_ => raptor);

        Assert.Single(_db.GetPendingSiblingWrites(10));
    }

    [Fact]
    public async Task SiblingDrain_DropsRowsForServersNoLongerConfigured()
    {
        _db.EnqueueSiblingPlaybackWrite("gone", "g1", 0, markPlayed: true);

        await NewSession().DrainSiblingWritesAsync(_ => null);

        Assert.Empty(_db.GetPendingSiblingWrites(10));
    }

    [Fact]
    public async Task PositionTicks_AreReportedToTheServerEveryTenSeconds()
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);

        _now = _now.AddSeconds(5);
        Assert.Null(session.OnPosition(5));

        _now = _now.AddSeconds(6);
        var report = session.OnPosition(11);
        Assert.NotNull(report);
        Assert.True(await report!);

        var body = Assert.Single(_http.Bodies("/Sessions/Playing/Progress"));
        Assert.Equal(11 * TicksPerSecond, body.GetProperty("PositionTicks").GetInt64());
        Assert.False(body.GetProperty("IsPaused").GetBoolean());

        _now = _now.AddSeconds(1);
        Assert.Null(session.OnPosition(12));
    }

    [Fact]
    public async Task Pause_ReportsProgressImmediately()
    {
        var session = NewSession();
        session.Begin(Movie(), _client, "ms1", 0);
        session.OnPosition(42);

        await session.ReportProgressAsync(isPaused: true);

        var body = Assert.Single(_http.Bodies("/Sessions/Playing/Progress"));
        Assert.True(body.GetProperty("IsPaused").GetBoolean());
        Assert.Equal(42 * TicksPerSecond, body.GetProperty("PositionTicks").GetInt64());
    }

    [Theory]
    [InlineData("eof", MpvEndReason.Eof)]
    [InlineData("stop", MpvEndReason.Stop)]
    [InlineData("quit", MpvEndReason.Quit)]
    [InlineData("error", MpvEndReason.Error)]
    [InlineData("redirect", MpvEndReason.Unknown)]
    [InlineData(null, MpvEndReason.Unknown)]
    public void EndFileReason_OnlyEofIsEof(string? raw, MpvEndReason expected)
    {
        Assert.Equal(expected, MpvController.ParseEndFileReason(raw));
    }

    [Fact]
    public async Task RemoveFromResume_SendsAnExplicitZeroPosition()
    {
        // Without PositionTicks the server treats the stop as played-to-completion.
        await _client.RemoveFromResumeAsync("m1");

        var req = Assert.Single(_http.Requests);
        Assert.Equal("DELETE", req.Method);
        Assert.EndsWith("/Users/user1/PlayingItems/m1?PositionTicks=0", req.Path);
    }

    [Fact]
    public void ClearResumeProgress_ZeroesPositionOnTheOwningServerOnly_AndKeepsPlayedState()
    {
        // Item ids are per-server: the same id on another server is a different item.
        _db.UpsertItem(new EmbyItem { Id = "1012702", Name = "Episode", Type = "Episode" }, "raptor");
        _db.UpsertItem(new EmbyItem { Id = "1012702", Name = "When Marnie Was There", Type = "Movie" }, "valkyrie");
        _db.UpdateLocalPlaybackProgress("raptor", "1012702", 600 * TicksPerSecond);
        _db.UpdateLocalPlaybackProgress("valkyrie", "1012702", 700 * TicksPerSecond);

        _db.ClearResumeProgress("1012702", "valkyrie");

        var cleared = _db.GetUserData("valkyrie", "1012702")!;
        Assert.Equal(0, cleared.PlaybackPositionTicks);
        Assert.False(cleared.Played);
        Assert.Equal(600 * TicksPerSecond, _db.GetUserData("raptor", "1012702")!.PlaybackPositionTicks);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Requests = new();
        public HttpStatusCode Status { get; init; } = HttpStatusCode.NoContent;
        public Func<string, string?> GetJson { get; init; } = _ => null; // body for a GET path, if any

        public IEnumerable<JsonElement> Bodies(string pathSuffix)
        {
            lock (Requests)
                return Requests.Where(r => r.Path.EndsWith(pathSuffix)).Select(r => JsonDocument.Parse(r.Body).RootElement).ToList();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content == null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.PathAndQuery, body));
            if (request.Method == HttpMethod.Get && GetJson(request.RequestUri!.AbsolutePath) is { } json)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(Status);
        }
    }
}
