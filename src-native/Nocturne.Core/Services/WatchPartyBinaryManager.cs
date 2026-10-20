using System.Diagnostics;
using System.IO.Compression;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class WatchPartyBinaryManager
{
    private const string FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/8.1.1/ffmpeg-8.1.1-essentials_build.zip";
    private const string CloudflaredUrl = "https://github.com/cloudflare/cloudflared/releases/download/2026.5.0/cloudflared-windows-amd64.exe";

    public string BinDir { get; }

    public WatchPartyBinaryManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        BinDir = Path.Combine(appData, "Nocturne", "bin");
    }

    public string GetFfmpegPath()
    {
        var local = Path.Combine(BinDir, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var baseLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(baseLocal)) return baseLocal;

        var fromPath = FindInPath("ffmpeg.exe");
        if (!string.IsNullOrEmpty(fromPath)) return fromPath;

        return local;
    }

    public string GetCloudflaredPath()
    {
        var local = Path.Combine(BinDir, "cloudflared.exe");
        if (File.Exists(local)) return local;

        var baseLocal = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cloudflared.exe");
        if (File.Exists(baseLocal)) return baseLocal;

        var fromPath = FindInPath("cloudflared.exe");
        if (!string.IsNullOrEmpty(fromPath)) return fromPath;

        return local;
    }

    public bool IsReady()
    {
        return File.Exists(GetFfmpegPath()) && File.Exists(GetCloudflaredPath());
    }

    public async Task<bool> EnsureBinariesAsync(IProgress<(string phase, int percent)>? progress = null, CancellationToken ct = default)
    {
        if (IsReady()) return true;

        Directory.CreateDirectory(BinDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var cfPath = Path.Combine(BinDir, "cloudflared.exe");
        if (!File.Exists(cfPath) && string.IsNullOrEmpty(FindInPath("cloudflared.exe")))
        {
            NocturneLogger.Info("WatchPartyBinaryManager", "Downloading cloudflared.exe...");
            progress?.Report(("cloudflared", 0));
            var tmpCf = Path.Combine(BinDir, "cloudflared.exe.tmp");
            try
            {
                await DownloadFileWithProgressAsync(http, CloudflaredUrl, tmpCf, pct => progress?.Report(("cloudflared", pct)), ct);
                if (File.Exists(cfPath)) File.Delete(cfPath);
                File.Move(tmpCf, cfPath);
                NocturneLogger.Info("WatchPartyBinaryManager", "cloudflared.exe installed successfully");
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("WatchPartyBinaryManager", "Failed to download cloudflared.exe", ex);
                if (File.Exists(tmpCf)) File.Delete(tmpCf);
                throw;
            }
        }

        var ffPath = Path.Combine(BinDir, "ffmpeg.exe");
        if (!File.Exists(ffPath) && string.IsNullOrEmpty(FindInPath("ffmpeg.exe")))
        {
            NocturneLogger.Info("WatchPartyBinaryManager", "Downloading ffmpeg build...");
            progress?.Report(("ffmpeg", 0));
            var tmpZip = Path.Combine(BinDir, "ffmpeg.zip.tmp");
            try
            {
                await DownloadFileWithProgressAsync(http, FfmpegUrl, tmpZip, pct => progress?.Report(("ffmpeg", pct)), ct);
                progress?.Report(("unzip", 50));
                NocturneLogger.Info("WatchPartyBinaryManager", "Extracting ffmpeg.exe from zip...");

                using (var archive = ZipFile.OpenRead(tmpZip))
                {
                    var ffmpegEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                    if (ffmpegEntry == null)
                    {
                        throw new FileNotFoundException("ffmpeg.exe not found inside the downloaded archive.");
                    }

                    if (File.Exists(ffPath)) File.Delete(ffPath);
                    ffmpegEntry.ExtractToFile(ffPath, overwrite: true);
                }

                File.Delete(tmpZip);
                progress?.Report(("unzip", 100));
                NocturneLogger.Info("WatchPartyBinaryManager", "ffmpeg.exe installed successfully");
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("WatchPartyBinaryManager", "Failed to download or extract ffmpeg.exe", ex);
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                throw;
            }
        }

        return IsReady();
    }

    private static async Task DownloadFileWithProgressAsync(HttpClient client, string url, string destPath, Action<int> onPercent, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var source = await response.Content.ReadAsStreamAsync(ct);
        using var dest = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long readSoFar = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await dest.WriteAsync(buffer, 0, read, ct);
            readSoFar += read;
            if (totalBytes > 0)
            {
                var pct = (int)((readSoFar * 100) / totalBytes);
                onPercent(Math.Clamp(pct, 0, 100));
            }
        }
    }

    private static string? FindInPath(string filename)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator);
        foreach (var p in paths)
        {
            try
            {
                var full = Path.Combine(p.Trim(), filename);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
