using System.IO.Compression;
using System.Security.Cryptography;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class WatchPartyBinaryManager
{
    // Pinned releases. Each download's SHA-256 is checked before it is installed, and IsReady re-hashes the
    // installed executables, which StartSessionAsync calls before starting them. Digests are the GitHub release
    // asset digests (cloudflared's also matches its release notes), confirmed against a download on 2026-10-21.
    // Only these copies in BinDir are run: the earlier fallback to any ffmpeg/cloudflared on PATH or next to the
    // app ran whatever it found.
    internal const string FfmpegUrl = "https://github.com/GyanD/codexffmpeg/releases/download/8.1.1/ffmpeg-8.1.1-essentials_build.zip";
    internal const string FfmpegZipSha256 = "6f58ce889f59c311410f7d2b18895b33c03456463486f3b1ebc93d97a0f54541";
    internal const string FfmpegExeSha256 = "228d7a8556258de907fdb55f36850078ebc7680b84ec30d84ea02e99bec1d1eb"; // bin/ffmpeg.exe in that zip
    internal const string CloudflaredUrl = "https://github.com/cloudflare/cloudflared/releases/download/2026.5.0/cloudflared-windows-amd64.exe";
    internal const string CloudflaredSha256 = "f141cded099c239171ad2cea6fb5da0fdaa2bd36104c3074d883f9546519eba7";

    public string BinDir { get; }

    public WatchPartyBinaryManager() : this(Path.Combine(AppPaths.DataDirectory, "bin"))
    {
    }

    internal WatchPartyBinaryManager(string binDir)
    {
        BinDir = binDir;
    }

    public string GetFfmpegPath() => Path.Combine(BinDir, "ffmpeg.exe");
    public string GetCloudflaredPath() => Path.Combine(BinDir, "cloudflared.exe");

    // Both executables present and matching their pinned SHA-256.
    public bool IsReady() => Matches(GetFfmpegPath(), FfmpegExeSha256) && Matches(GetCloudflaredPath(), CloudflaredSha256);

    public async Task<bool> EnsureBinariesAsync(IProgress<(string phase, int percent)>? progress = null, CancellationToken ct = default)
    {
        if (IsReady()) return true;

        Directory.CreateDirectory(BinDir);

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        var cfPath = GetCloudflaredPath();
        if (!Matches(cfPath, CloudflaredSha256))
        {
            NocturneLogger.Info("WatchPartyBinaryManager", "Downloading cloudflared.exe...");
            progress?.Report(("cloudflared", 0));
            var tmpCf = cfPath + ".tmp";
            try
            {
                await DownloadFileWithProgressAsync(http, CloudflaredUrl, tmpCf, pct => progress?.Report(("cloudflared", pct)), ct);
                Verify(tmpCf, CloudflaredSha256, "cloudflared download");
                File.Move(tmpCf, cfPath, overwrite: true);
                NocturneLogger.Info("WatchPartyBinaryManager", "cloudflared.exe installed and verified");
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("WatchPartyBinaryManager", "Failed to install cloudflared.exe", ex);
                if (File.Exists(tmpCf)) File.Delete(tmpCf);
                throw;
            }
        }

        var ffPath = GetFfmpegPath();
        if (!Matches(ffPath, FfmpegExeSha256))
        {
            NocturneLogger.Info("WatchPartyBinaryManager", "Downloading ffmpeg build...");
            progress?.Report(("ffmpeg", 0));
            var tmpZip = Path.Combine(BinDir, "ffmpeg.zip.tmp");
            var tmpExe = ffPath + ".tmp";
            try
            {
                await DownloadFileWithProgressAsync(http, FfmpegUrl, tmpZip, pct => progress?.Report(("ffmpeg", pct)), ct);
                Verify(tmpZip, FfmpegZipSha256, "ffmpeg download");
                progress?.Report(("unzip", 50));
                NocturneLogger.Info("WatchPartyBinaryManager", "Extracting ffmpeg.exe from zip...");

                using (var archive = ZipFile.OpenRead(tmpZip))
                {
                    var ffmpegEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                        ?? throw new FileNotFoundException("ffmpeg.exe not found inside the downloaded archive.");
                    ffmpegEntry.ExtractToFile(tmpExe, overwrite: true);
                }
                Verify(tmpExe, FfmpegExeSha256, "extracted ffmpeg.exe");
                File.Move(tmpExe, ffPath, overwrite: true);

                File.Delete(tmpZip);
                progress?.Report(("unzip", 100));
                NocturneLogger.Info("WatchPartyBinaryManager", "ffmpeg.exe installed and verified");
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("WatchPartyBinaryManager", "Failed to install ffmpeg.exe", ex);
                if (File.Exists(tmpZip)) File.Delete(tmpZip);
                if (File.Exists(tmpExe)) File.Delete(tmpExe);
                throw;
            }
        }

        return IsReady();
    }

    internal static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool Matches(string path, string sha256) => File.Exists(path) && Sha256Of(path) == sha256;

    internal static void Verify(string path, string expectedSha256, string what)
    {
        var actual = Sha256Of(path);
        if (actual != expectedSha256)
            throw new InvalidDataException($"{what} failed its SHA-256 check (expected {expectedSha256}, got {actual}); it was not installed.");
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
}
