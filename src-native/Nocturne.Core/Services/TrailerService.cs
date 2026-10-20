using System.IO;
using System.Text.RegularExpressions;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Videos.Streams;

namespace Nocturne.Core.Services;

public class TrailerService
{
    private static readonly YoutubeClient _youtubeClient = new();
    private static readonly SemaphoreSlim _downloadLock = new(1, 1);

    private static readonly Regex YouTubeRegex = new(
        @"(?:youtu\.be\/|youtube\.com\/(?:embed\/|v\/|watch\?v=|watch\?.+&v=))([\w-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string? ExtractYouTubeId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var match = YouTubeRegex.Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    public async Task<string?> ResolveTrailerStreamUrlAsync(
        EmbyItem item,
        EmbyClient? client = null,
        string? serverUrl = null,
        string? token = null)
    {
        if (item == null) return null;

        // 1. Try Local Trailers from Emby first
        if (client != null && !string.IsNullOrEmpty(item.Id))
        {
            try
            {
                var localTrailers = await client.GetLocalTrailersAsync(item.Id);
                var local = localTrailers.FirstOrDefault();
                if (local != null)
                {
                    var srv = serverUrl ?? client.BaseUrl;
                    var tok = token ?? client.Token;
                    var baseClean = srv.TrimEnd('/');
                    var prefix = baseClean.EndsWith("/emby", StringComparison.OrdinalIgnoreCase) ? baseClean : $"{baseClean}/emby";
                    var streamUrl = $"{prefix}/Videos/{local.Id}/stream.mp4?Static=true&api_key={tok}";
                    NocturneLogger.Info("TrailerService", $"Using local Emby trailer stream for '{item.Name}'");
                    return streamUrl;
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("TrailerService", $"Local trailer check failed: {ex.Message}");
            }
        }

        // 2. Direct video files (.mp4, .webm, .m4v) in RemoteTrailers
        if (item.RemoteTrailers != null && item.RemoteTrailers.Count > 0)
        {
            var direct = item.RemoteTrailers.FirstOrDefault(t =>
                !string.IsNullOrWhiteSpace(t.Url) &&
                (t.Url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
                 t.Url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase) ||
                 t.Url.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)));

            if (direct != null && !string.IsNullOrWhiteSpace(direct.Url))
            {
                NocturneLogger.Info("TrailerService", $"Using direct video trailer for '{item.Name}'");
                return direct.Url;
            }
        }

        // 3. YouTube Trailer (from RemoteTrailers or via YouTube search fallback)
        string? videoId = null;
        if (item.RemoteTrailers != null && item.RemoteTrailers.Count > 0)
        {
            var youtubeTrailer = item.RemoteTrailers.FirstOrDefault(t => !string.IsNullOrWhiteSpace(ExtractYouTubeId(t.Url)));
            if (youtubeTrailer != null)
            {
                videoId = ExtractYouTubeId(youtubeTrailer.Url);
            }
        }

        // If no RemoteTrailer URL in Emby metadata, search YouTube for the official trailer
        if (string.IsNullOrEmpty(videoId) && !string.IsNullOrWhiteSpace(item.Name))
        {
            try
            {
                var query = $"{item.Name} {item.ProductionYear} official trailer";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var searchResults = await _youtubeClient.Search.GetVideosAsync(query, cts.Token);
                var first = searchResults.FirstOrDefault();
                if (first != null)
                {
                    videoId = first.Id.Value;
                    NocturneLogger.Info("TrailerService", $"Discovered YouTube trailer via search for '{item.Name}' ({videoId})");
                }
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("TrailerService", $"YouTube search trailer lookup failed for '{item.Name}': {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(videoId))
        {
            return null;
        }

        // Check local disk cache (AppData/Local/Nocturne/cache/trailers/{videoId}.mp4)
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Nocturne", "cache", "trailers");
        Directory.CreateDirectory(cacheDir);
        var cachedFile = Path.Combine(cacheDir, $"{videoId}.mp4");

        if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 100_000)
        {
            NocturneLogger.Info("TrailerService", $"Using cached local trailer file for '{item.Name}' ({cachedFile})");
            return cachedFile;
        }

        // Download video stream to local file so WPF MediaElement plays it with 100% reliability (bypasses 403 Forbidden)
        await _downloadLock.WaitAsync();
        try
        {
            if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 100_000)
            {
                return cachedFile;
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId, cts.Token);

            // Prefer muxed streams (e.g. 720p/360p) or highest video-only MP4
            var streamInfo = manifest.GetMuxedStreams().GetWithHighestVideoQuality()
                          ?? (IVideoStreamInfo?)manifest.GetVideoOnlyStreams()
                                                       .Where(s => s.Container == Container.Mp4)
                                                       .GetWithHighestVideoQuality()
                          ?? manifest.GetVideoOnlyStreams().GetWithHighestVideoQuality();

            if (streamInfo != null)
            {
                var tempFile = cachedFile + ".tmp";
                await _youtubeClient.Videos.Streams.DownloadAsync(streamInfo, tempFile, cancellationToken: cts.Token);
                if (File.Exists(cachedFile)) File.Delete(cachedFile);
                File.Move(tempFile, cachedFile);
                NocturneLogger.Info("TrailerService", $"Downloaded and cached trailer for '{item.Name}' ({cachedFile})");
                return cachedFile;
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("TrailerService", $"Could not download YouTube trailer ({videoId}): {ex.Message}");
        }
        finally
        {
            _downloadLock.Release();
        }

        return null;
    }
}
