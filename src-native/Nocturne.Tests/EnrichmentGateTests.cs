using Nocturne.Core.Models;
using Nocturne.Core.Services;
using Xunit;

namespace Nocturne.Tests;

// #30, D12: every TMDB pass, the Settings one included, goes through one gate: stopped by playback or a sync, resumed
// when they end, with the same progress reporting.
public class EnrichmentGateTests
{
    // Runs until cancelled; records each run.
    private sealed class Tmdb : ITmdbEnrichmentService
    {
        public readonly List<(IProgress<EnrichProgress> Progress, CancellationToken Token)> Runs = new();

        public async Task RunAsync(IProgress<EnrichProgress> p, CancellationToken ct)
        {
            lock (Runs) Runs.Add((p, ct));
            try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        }

        public int RunCount { get { lock (Runs) return Runs.Count; } }
        public Task<TmdbMetadata?> GetAsync(string providerKey, CancellationToken ct) => Task.FromResult<TmdbMetadata?>(null);
        public string GetActiveApiKey() => "k";
        public string? CustomApiKey => "k";
        public bool HasBundledKey => false;
        public void SetCustomApiKey(string? apiKey) { }
        public int EnrichedCount => 0;
        public int PendingCount => 0;
        public int NotFoundCount => 0;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task TheSettingsRun_PausesForPlayback_AndResumesAfter_ReportingToTheSamePlace()
    {
        var tmdb = new Tmdb();
        var gate = new EnrichmentGate(tmdb);
        var settings = new Progress<EnrichProgress>();

        var pass = gate.RunAsync(settings);
        await Until(() => tmdb.RunCount == 1);

        gate.SetPlaying(true);
        await pass.WaitAsync(TimeSpan.FromSeconds(5)); // a pass playback does not stop never ends: fail, not hang
        Assert.True(tmdb.Runs[0].Token.IsCancellationRequested);
        Assert.True(gate.IsDeferred);

        gate.SetPlaying(false);
        await Until(() => tmdb.RunCount == 2);
        Assert.Same(settings, tmdb.Runs[1].Progress);
        Assert.False(tmdb.Runs[1].Token.IsCancellationRequested);
        Assert.False(gate.IsDeferred);
    }

    [Fact]
    public async Task APassAskedForDuringPlaybackAndASync_WaitsForBoth()
    {
        var tmdb = new Tmdb();
        var gate = new EnrichmentGate(tmdb);
        gate.SetPlaying(true);
        gate.SetSyncing(true);

        await gate.RunAsync();
        gate.SetPlaying(false);
        await Task.Delay(50);
        Assert.Equal(0, tmdb.RunCount);

        gate.SetSyncing(false);
        await Until(() => tmdb.RunCount == 1);
    }

    [Fact]
    public async Task PlaybackWithNoPassRunning_StartsNothing()
    {
        var tmdb = new Tmdb();
        var gate = new EnrichmentGate(tmdb);
        gate.SetPlaying(true);
        gate.SetPlaying(false);
        await Task.Delay(50);
        Assert.Equal(0, tmdb.RunCount);
    }
}
