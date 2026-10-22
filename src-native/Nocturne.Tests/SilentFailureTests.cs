using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

// Stage D: failures that used to be silent (index load, settings load, DPAPI, the shared v3.5 files) and the
// rebuild's lock and writer exclusion.
public class SilentFailureTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");

    public SilentFailureTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static EmbyItem Movie(string id, string server, string tmdb) =>
        new() { Id = id, ServerId = server, Name = "Movie " + tmdb, Type = "Movie", ProviderIds = new() { { "Tmdb", tmdb } } };

    // D1
    [Fact]
    public void IndexLoadFailure_Throws_AndKeepsTheIndexItHad()
    {
        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var index = new ConcurrentDedupIndex();
        var items = new[] { Movie("a", "s1", "1"), Movie("b", "s1", "2"), Movie("c", "s1", "3") };
        foreach (var it in items) db.UpsertItem(it, "s1");
        index.ProcessBatch(items, db);
        db.ExecuteSql("DROP TABLE dedup_groups;"); // sync start reads the next group id from it

        Assert.ThrowsAny<Exception>(() => index.InitializeFromDb(db));

        Assert.Equal(3, index.TotalGroupsCount);
        var next = Movie("d", "s1", "4");
        index.ProcessBatch(new[] { next });
        Assert.Equal("4", next.DedupGroupId); // not 1: ids keep counting from the loaded state
    }

    // D2
    [Fact]
    public void UnreadableSettings_AreReported_AndNeverSavedOver()
    {
        var path = Path.Combine(_dir, SettingsService.SettingsFileName);
        File.WriteAllText(path, "{ \"Servers\": [ { truncated");

        var settings = new SettingsService(path);
        settings.Update(s => s.SyncStatus = "complete");

        Assert.True(settings.LoadFailed);
        Assert.NotNull(settings.LoadError);
        Assert.Equal("{ \"Servers\": [ { truncated", File.ReadAllText(path));
    }

    [Fact]
    public void AcceptDefaults_KeepsTheUnreadableFile_ThenSavesAtomically()
    {
        var path = Path.Combine(_dir, SettingsService.SettingsFileName);
        File.WriteAllText(path, "not json");
        var settings = new SettingsService(path);

        var aside = settings.AcceptDefaults();
        settings.Update(s => s.SyncStatus = "complete");

        Assert.Equal("not json", File.ReadAllText(aside));
        Assert.Equal("complete", new SettingsService(path).Current.SyncStatus);
        Assert.False(File.Exists(path + ".tmp"));
    }

    // D3
    [Fact]
    public void Unprotect_RejectsDataDpapiDidNotProduce()
    {
        Assert.ThrowsAny<CryptographicException>(() => CredentialProtection.Unprotect(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.Equal("tok", CredentialProtection.UnprotectString(CredentialProtection.ProtectString("tok")));
    }

    // D4
    [Fact]
    public void SharedFileWithV4Schema_MovesToV4sOwnFile()
    {
        var legacy = Path.Combine(_dir, "nocturne.db");
        using (var db = new DatabaseService(legacy)) db.UpsertItem(Movie("a", "s1", "1"), "s1");
        SqliteConnection.ClearAllPools();

        var path = DatabaseService.ResolveDefaultPath(_dir);

        Assert.Equal(Path.Combine(_dir, DatabaseService.DatabaseFileName), path);
        Assert.False(File.Exists(legacy));
        using var moved = new DatabaseService(path);
        Assert.Single(moved.GetCachedItems(serverId: "s1"));
    }

    [Fact]
    public void SharedV35File_IsLeftAlone_AndV4NoLongerDropsItsTables()
    {
        var legacy = Path.Combine(_dir, "nocturne.db");
        using (var conn = new SqliteConnection($"Data Source={legacy};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE items (id TEXT); CREATE TABLE image_cache (url TEXT);";
            cmd.ExecuteNonQuery();
        }

        Assert.Equal(Path.Combine(_dir, DatabaseService.DatabaseFileName), DatabaseService.ResolveDefaultPath(_dir));
        Assert.True(File.Exists(legacy));

        // Even opened in place (the fallback when a move is unsafe), v3.5's tables survive.
        using (new DatabaseService(legacy)) { }
        SqliteConnection.ClearAllPools();
        using var check = new SqliteConnection($"Data Source={legacy};Pooling=False");
        check.Open();
        using var q = check.CreateCommand();
        q.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('items', 'image_cache');";
        Assert.Equal(2L, (long)q.ExecuteScalar()!);
    }

    [Theory]
    [InlineData("{ \"Servers\": [], \"Theme\": \"dark\" }", true)]   // v4 wrote PascalCase
    [InlineData("{ \"servers\": [], \"theme\": \"dark\" }", false)]  // v3.5's camelCase file
    public void SharedSettings_AreCopiedOnlyWhenInV4Format(string json, bool copied)
    {
        File.WriteAllText(Path.Combine(_dir, "nocturne-settings.json"), json);

        var path = SettingsService.ResolveDefaultPath(_dir);

        Assert.Equal(copied, File.Exists(path));
        Assert.True(File.Exists(Path.Combine(_dir, "nocturne-settings.json")));
        Assert.Equal(copied ? "dark" : "auto", new SettingsService(path).Current.Theme);
    }

    [Fact]
    public void ClearAllCache_ClearsThisDatabase()
    {
        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        db.UpsertItem(Movie("a", "s1", "1"), "s1");

        db.ClearAllCache();

        Assert.Empty(db.GetCachedItems(serverId: "s1"));
    }

    // Rebuild locking
    [Fact]
    public void RebuildBuildPhase_LeavesTheDatabaseLockFree()
    {
        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var stop = new InvalidOperationException("stop before the swap");

        var thrown = Assert.Throws<InvalidOperationException>(() => db.SwapInRebuild(() =>
        {
            var read = Task.Run(() => db.GetCachedContinueWatching());
            Assert.True(read.Wait(TimeSpan.FromSeconds(5)), "a read on another thread blocked during the build");
            throw stop;
        }));

        Assert.Same(stop, thrown);
    }

    [Fact]
    public async Task RebuildAndSync_WaitForEachOther()
    {
        using var db = new DatabaseService(Path.Combine(_dir, "a.db"));
        var dedup = new DedupEngine();
        var settings = new SettingsService(Path.Combine(_dir, SettingsService.SettingsFileName));
        var sync = new SyncEngine(new EmbyClient(), db, dedup, new TraktService(new TraktAuthService(db)), settings);

        dedup.WriterGate.Wait(); // a rebuild (or sync) in progress
        var rebuild = Task.Run(() => dedup.RebuildIndex(db));
        var syncRun = sync.RunFullSyncAsync();
        await Task.Delay(300);
        Assert.False(rebuild.IsCompleted);
        Assert.False(syncRun.IsCompleted);

        dedup.WriterGate.Release();
        await Task.WhenAll(rebuild, syncRun).WaitAsync(TimeSpan.FromSeconds(20));
    }
}
