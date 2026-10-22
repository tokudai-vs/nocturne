using System.IO;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #22 item 4: drift is measured after an abnormal sync (partial, failed, interrupted, resumed) and monthly, not weekly.
public class DriftTriggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly FakeEmbyServer _fake = new();
    private readonly DatabaseService _db;
    private readonly SyncEngine _sync;

    public DriftTriggerTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        settings.Update(s =>
        {
            s.Servers.Add(new ServerConfig { Id = "raptor", Name = "Raptor", Url = "http://raptor.test", AccessToken = "t", UserId = "u" });
            s.Servers.Add(new ServerConfig { Id = "valkyrie", Name = "Valkyrie", Url = "http://valkyrie.test", AccessToken = "t", UserId = "u" });
        });
        EmbyClient ClientFor(ServerConfig s) { var c = new EmbyClient(_fake); c.Configure(s.Url, s.AccessToken, s.UserId); return c; }
        _sync = new SyncEngine(new EmbyClient(), _db, new DedupEngine(), new TraktService(new TraktAuthService(_db)), settings, ClientFor);
        foreach (var host in new[] { "raptor.test", "valkyrie.test" })
            _fake.Libraries[host] = Enumerable.Range(1, 3).Select(i => new EmbyItem { Id = $"{host[0]}{i}", Name = $"Movie {i}", Type = "Movie", ProviderIds = new() { ["Tmdb"] = i.ToString() } }).ToList();
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private bool Measured() => _db.GetMigrationState(DedupEngine.DriftMeasuredKey) != null;
    private Task Sync() => _sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));

    [Fact]
    public async Task CleanSyncs_DoNotMeasure_TheBackstopDoesAfterAMonth()
    {
        await Sync();
        await Sync();
        Assert.False(Measured());
        Assert.NotNull(_db.GetMigrationState(SyncEngine.DriftClockKey)); // the backstop's clock started

        _db.SetMigrationState(SyncEngine.DriftClockKey, DateTime.UtcNow.AddDays(-31).ToString("o"));
        await Sync();
        Assert.True(Measured());
    }

    [Fact]
    public async Task APartialSync_IsMeasured()
    {
        _fake.Fail = uri => uri.Host == "raptor.test" && uri.AbsolutePath.EndsWith("/Views");
        await Sync();
        Assert.True(Measured());
    }

    [Fact]
    public async Task ASyncAfterAnInterruptedOne_IsMeasured()
    {
        _db.SetMigrationState(SyncEngine.SyncRunningKey, "1"); // as a run killed mid-way leaves it
        await Sync();
        Assert.True(Measured());
        Assert.Equal("0", _db.GetMigrationState(SyncEngine.SyncRunningKey));
    }

    [Fact]
    public async Task ASyncResumedFromACheckpoint_IsMeasured()
    {
        _db.SaveSyncCheckpoint("raptor", "lib", 1, 3, isCompleted: false);
        await Sync();
        Assert.True(Measured());
    }
}
