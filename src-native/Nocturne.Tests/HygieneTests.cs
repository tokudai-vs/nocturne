using System.IO;
using Microsoft.Data.Sqlite;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Nocturne.Core.Utils;
using Xunit;

namespace Nocturne.Tests;

// Stage E: the year column, the self-check, log redaction / rotation / path, and the silent DB catches.
public class HygieneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
    private readonly DatabaseService _db;

    public HygieneTests()
    {
        Directory.CreateDirectory(_dir);
        _db = new DatabaseService(Path.Combine(_dir, "a.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    // E1
    [Fact]
    public void SiblingLookupByNameAndYear_Runs()
    {
        var a = new EmbyItem { Id = "a", Name = "Heat", Type = "Movie", ProductionYear = 1995 };
        var b = new EmbyItem { Id = "b", Name = "Heat", Type = "Movie", ProductionYear = 1995 };
        _db.UpsertItem(a, "s1");
        _db.UpsertItem(b, "s2");

        var siblings = _db.GetSiblingVersions(new EmbyItem { Id = "a", Name = "Heat", ProductionYear = 1995 });

        Assert.Contains(siblings, s => s.Id == "b");
    }

    // E2
    [Fact]
    public void SelfCheck_PreparesTheStatementsTheMethodsRun_AndNamesAFailingOne()
    {
        Assert.True(DatabaseService.RegisteredStatements.Count > 80);
        Assert.Contains(DatabaseService.RegisteredStatements, sql => sql.Contains("LOWER(name) = $name AND production_year = $year"));

        _db.ExecuteSql("ALTER TABLE user_data DROP COLUMN origin;");
        var ex = Assert.Throws<InvalidOperationException>(() => _db.SelfCheckPreparedStatements());
        Assert.Contains("no such column: origin", ex.Message);
    }

    // E3
    [Theory]
    [InlineData("https://em3.example/emby/Videos/1/stream?static=true&api_key=abc123&MediaSourceId=x", "api_key=<redacted>&MediaSourceId=x")]
    [InlineData("{\"access_token\":\"tok1\",\"refresh_token\":\"tok2\",\"expires_in\":7776000}", "\"access_token\":\"<redacted>\",\"refresh_token\":\"<redacted>\",\"expires_in\":7776000")]
    [InlineData("device_code=dc1&code_verifier=v1&client_id=cid", "device_code=<redacted>&code_verifier=<redacted>&client_id=cid")]
    [InlineData("MediaBrowser Client=\"Nocturne\", Token=\"t0k\"", "Token=\"<redacted>\"")]
    [InlineData("X-Emby-Token: abc", "X-Emby-Token: <redacted>")]
    public void Redact_HidesCredentials(string input, string expectedFragment)
    {
        var redacted = NocturneLogger.Redact(input);
        Assert.Contains(expectedFragment, redacted);
        Assert.DoesNotContain("abc123", redacted);
    }

    [Fact]
    public void Redact_LeavesOrdinaryTextAlone()
    {
        const string line = "Failed to decrypt access token; user_code ABCD-1234 shown to the user; TraktTokenExpiresAt unchanged";
        Assert.Equal(line, NocturneLogger.Redact(line));
    }

    [Fact]
    public void Log_RotatesPastTheSizeLimit()
    {
        var original = NocturneLogger.LogPath;
        var limit = NocturneLogger.MaxLogBytes;
        var path = Path.Combine(_dir, "rot.log");
        try
        {
            NocturneLogger.LogPath = path;
            NocturneLogger.MaxLogBytes = 200;
            for (int i = 0; i < 10; i++) NocturneLogger.Info("Test", new string('x', 50));

            Assert.True(File.Exists(path + ".1"));
            Assert.True(new FileInfo(path).Length < 400);
        }
        finally
        {
            NocturneLogger.MaxLogBytes = limit;
            NocturneLogger.LogPath = original;
        }
    }

    // E4
    [Fact]
    public void TestsLogOutsideTheUsersDataDirectory()
    {
        Assert.StartsWith(TestSetup.DataDirectory, NocturneLogger.LogPath);
        Assert.Equal(TestSetup.DataDirectory, AppPaths.DataDirectory);
    }

    // E5 (tests build Debug: the failure propagates instead of reading as "no data")
    [Fact]
    public void FailedRead_IsLoggedWithItsStatement_AndSurfacesInDebug()
    {
        _db.ExecuteSql("DROP TABLE user_data;");

        var ex = Assert.ThrowsAny<SqliteException>(() => _db.GetUserData("s1", "a"));

        Assert.Contains("no such table: user_data", ex.Message);
        Assert.Contains("GetUserData failed: SELECT", File.ReadAllText(NocturneLogger.LogPath));
    }
}
