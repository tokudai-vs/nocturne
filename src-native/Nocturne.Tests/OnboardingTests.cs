using System.IO;
using System.Net;
using System.Net.Http;
using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// Stage H: what the user is told when a server can't be reached, during onboarding and during the first sync.
public class OnboardingTests
{
    private sealed class FailingHandler : HttpMessageHandler
    {
        public Exception? Throw { get; init; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Throw != null ? Task.FromException<HttpResponseMessage>(Throw) : Task.FromResult(new HttpResponseMessage(Status));
    }

    [Fact]
    public async Task ConnectionTest_ReportsWhyItFailed()
    {
        var dns = new EmbyClient(new FailingHandler { Throw = new HttpRequestException("No such host is known. (emby.example:8096)") });
        var (info, error) = await dns.CheckServerAsync("http://emby.example:8096");
        Assert.Null(info);
        Assert.Contains("No such host is known", error);

        var notEmby = new EmbyClient(new FailingHandler { Status = HttpStatusCode.NotFound });
        Assert.Contains("HTTP 404", (await notEmby.CheckServerAsync("http://192.168.1.5:8096")).Error);
    }

    [Fact]
    public async Task FirstSync_WithAnUnreachableServer_EndsPartial_AndNamesIt()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"nocturne_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            using var db = new DatabaseService(Path.Combine(dir, "a.db"));
            var settings = new SettingsService(Path.Combine(dir, SettingsService.SettingsFileName));
            settings.Update(s => s.Servers.Add(new ServerConfig { Id = "v1", Name = "Valkyrie", Url = "http://127.0.0.1:9", AccessToken = "t", UserId = "u" }));
            var sync = new SyncEngine(new EmbyClient(), db, new DedupEngine(), new TraktService(settings), settings);
            var reports = new List<SyncProgressReport>();
            var completed = false;
            sync.ProgressReported += r => { lock (reports) reports.Add(r); };
            sync.SyncCompleted += () => completed = true;

            await sync.RunFullSyncAsync().WaitAsync(TimeSpan.FromSeconds(60));

            var last = reports.Last();
            Assert.Equal(SyncState.Partial, last.State);
            Assert.Contains("Valkyrie", last.StatusMessage);
            Assert.False(completed); // not "completed successfully"
            Assert.Equal("partial", settings.Current.SyncStatus);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
