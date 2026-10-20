using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public enum WatchPartySessionState
{
    Idle,
    Starting,
    Waiting,
    Live,
    Ended,
    Error
}

public class WatchPartyService : IDisposable
{
    private readonly WatchPartyBinaryManager _binaryManager;
    private readonly List<WebSocket> _wsClients = new();
    private HttpListener? _listener;
    private Process? _ffmpegProcess;
    private Process? _tunnelProcess;
    private Timer? _heartbeatTimer;
    private string? _sessionDir;
    private bool _disposed;

    public WatchPartySessionState State { get; private set; } = WatchPartySessionState.Idle;
    public string? SessionId { get; private set; }
    public string? Title { get; private set; }
    public double DurationSec { get; private set; }
    public double StartOffsetSec { get; private set; }
    public long StartedAtMs { get; private set; }
    public string? LocalUrl { get; private set; }
    public string? TunnelUrl { get; private set; }
    public double TranscodedSeconds { get; private set; }
    public int GuestCount { get; private set; }
    public double CurrentPosition { get; private set; }
    public bool IsPlaying { get; private set; }
    public string? ErrorMessage { get; private set; }

    public bool IsActive => State == WatchPartySessionState.Waiting || State == WatchPartySessionState.Live;

    public event EventHandler<WatchPartySessionState>? StateChanged;
    public event EventHandler<int>? GuestCountChanged;
    public event EventHandler<double>? TranscodeProgressChanged;
    public event EventHandler<string>? TunnelUrlResolved;

    public WatchPartyService(WatchPartyBinaryManager binaryManager)
    {
        _binaryManager = binaryManager;
    }

    public bool AreBinariesReady() => _binaryManager.IsReady();

    public Task<bool> EnsureBinariesAsync(IProgress<(string phase, int percent)>? progress = null, CancellationToken ct = default)
    {
        return _binaryManager.EnsureBinariesAsync(progress, ct);
    }

    public async Task<string> StartSessionAsync(
        string title,
        string streamUrl,
        double durationSec,
        double startOffsetSec = 0,
        int targetHeight = 1080,
        CancellationToken ct = default)
    {
        if (IsActive)
        {
            await StopSessionAsync();
        }

        State = WatchPartySessionState.Starting;
        ErrorMessage = null;
        StateChanged?.Invoke(this, State);

        try
        {
            if (!_binaryManager.IsReady())
            {
                var ready = await _binaryManager.EnsureBinariesAsync(null, ct);
                if (!ready)
                {
                    throw new InvalidOperationException("Watch Party binaries (ffmpeg/cloudflared) could not be verified.");
                }
            }

            SessionId = Guid.NewGuid().ToString("N");
            Title = title;
            DurationSec = durationSec;
            StartOffsetSec = startOffsetSec;
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            CurrentPosition = startOffsetSec;
            IsPlaying = false;
            TranscodedSeconds = 0;

            // Temp session directory
            _sessionDir = Path.Combine(Path.GetTempPath(), "Nocturne", $"WatchParty_{SessionId}");
            Directory.CreateDirectory(_sessionDir);

            // Find free port and start HttpListener
            var port = GetAvailablePort();
            LocalUrl = $"http://127.0.0.1:{port}";

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            // Run listener pump in background
            _ = RunHttpServerAsync(_listener);

            // Spawn FFmpeg transcoder
            StartTranscoder(streamUrl, targetHeight, startOffsetSec);

            // Spawn Cloudflared tunnel
            var tunnelTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            StartTunnel(port, tunnelTcs);

            // Wait for tunnel URL with timeout (30s)
            var completedTask = await Task.WhenAny(tunnelTcs.Task, Task.Delay(30000, ct));
            if (completedTask == tunnelTcs.Task)
            {
                TunnelUrl = await tunnelTcs.Task;
                TunnelUrlResolved?.Invoke(this, TunnelUrl);
            }
            else
            {
                NocturneLogger.Warn("WatchPartyService", "Tunnel URL resolution timed out, continuing with LocalUrl fallback");
                TunnelUrl = LocalUrl;
            }

            State = WatchPartySessionState.Waiting;
            StateChanged?.Invoke(this, State);

            // Setup 1-second heartbeat timer
            _heartbeatTimer = new Timer(_ =>
            {
                if (IsActive && _wsClients.Count > 0)
                {
                    BroadcastHeartbeat(CurrentPosition, IsPlaying);
                }
            }, null, 1000, 1000);

            NocturneLogger.Info("WatchPartyService", $"Watch party session started! Local: {LocalUrl}, Tunnel: {TunnelUrl}");
            return TunnelUrl ?? LocalUrl;
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("WatchPartyService", "Failed to start watch party session", ex);
            ErrorMessage = ex.Message;
            State = WatchPartySessionState.Error;
            StateChanged?.Invoke(this, State);
            await StopSessionAsync();
            throw;
        }
    }

    public void MarkShowLive()
    {
        if (State == WatchPartySessionState.Waiting)
        {
            State = WatchPartySessionState.Live;
            StateChanged?.Invoke(this, State);
            BroadcastPlay(CurrentPosition);
        }
    }

    public void BroadcastPlay(double position)
    {
        IsPlaying = true;
        CurrentPosition = position;
        _ = BroadcastAsync(new
        {
            type = "play",
            position,
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public void BroadcastPause(double position)
    {
        IsPlaying = false;
        CurrentPosition = position;
        _ = BroadcastAsync(new
        {
            type = "pause",
            position,
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public void BroadcastSeek(double position)
    {
        CurrentPosition = position;
        _ = BroadcastAsync(new
        {
            type = "seek",
            position,
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public void BroadcastHeartbeat(double position, bool playing)
    {
        IsPlaying = playing;
        CurrentPosition = position;
        _ = BroadcastAsync(new
        {
            type = "heartbeat",
            position,
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public async Task StopSessionAsync()
    {
        if (State == WatchPartySessionState.Idle && _listener == null && _ffmpegProcess == null) return;

        State = WatchPartySessionState.Ended;
        StateChanged?.Invoke(this, State);

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        try
        {
            await BroadcastAsync(new { type = "session_end" });
        }
        catch { }

        lock (_wsClients)
        {
            foreach (var ws in _wsClients)
            {
                try { ws.Dispose(); } catch { }
            }
            _wsClients.Clear();
        }
        GuestCount = 0;
        GuestCountChanged?.Invoke(this, 0);

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;

        // Terminate FFmpeg
        if (_ffmpegProcess != null && !_ffmpegProcess.HasExited)
        {
            try
            {
                _ffmpegProcess.StandardInput.WriteLine("q");
                if (!_ffmpegProcess.WaitForExit(1000))
                {
                    _ffmpegProcess.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { _ffmpegProcess.Kill(entireProcessTree: true); } catch { }
            }
            _ffmpegProcess.Dispose();
            _ffmpegProcess = null;
        }

        // Terminate Cloudflared
        if (_tunnelProcess != null && !_tunnelProcess.HasExited)
        {
            try
            {
                _tunnelProcess.Kill(entireProcessTree: true);
            }
            catch { }
            _tunnelProcess.Dispose();
            _tunnelProcess = null;
        }

        // Clean up temp session files
        if (!string.IsNullOrEmpty(_sessionDir) && Directory.Exists(_sessionDir))
        {
            try
            {
                Directory.Delete(_sessionDir, recursive: true);
            }
            catch { }
            _sessionDir = null;
        }

        State = WatchPartySessionState.Idle;
        TunnelUrl = null;
        LocalUrl = null;
        SessionId = null;
        Title = null;
        StateChanged?.Invoke(this, State);
    }

    private void StartTranscoder(string sourceUrl, int targetHeight, double startOffsetSec)
    {
        var ffmpegExe = _binaryManager.GetFfmpegPath();
        var playlistPath = Path.Combine(_sessionDir!, "stream.m3u8");
        var segmentPattern = Path.Combine(_sessionDir!, "segment_%05d.ts");

        var seekArg = startOffsetSec > 0 ? $"-ss {startOffsetSec:F2} " : "";
        var bitrate = targetHeight >= 2160 ? "20M" : targetHeight >= 1080 ? "5M" : "2.5M";
        var bufsize = targetHeight >= 2160 ? "40M" : targetHeight >= 1080 ? "10M" : "5M";

        var args = $"-y -hide_banner -loglevel error -stats -probesize 1M -analyzeduration 1M -fflags +nobuffer " +
                   $"{seekArg}-i \"{sourceUrl}\" " +
                   $"-c:v libx264 -preset veryfast -b:v {bitrate} -maxrate {bitrate} -bufsize {bufsize} " +
                   $"-vf \"scale=-2:min({targetHeight}\\,ih)\" -pix_fmt yuv420p " +
                   $"-force_key_frames \"expr:gte(t,n_forced*4)\" -sc_threshold 0 " +
                   $"-c:a aac -b:a 160k -ac 2 " +
                   $"-f hls -hls_time 4 -hls_playlist_type event -hls_flags independent_segments " +
                   $"-hls_segment_filename \"{segmentPattern}\" \"{playlistPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegExe,
            Arguments = args,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };

        _ffmpegProcess = new Process { StartInfo = psi };
        _ffmpegProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                var match = Regex.Match(e.Data, @"time=(\d{2}):(\d{2}):(\d{2})(?:\.(\d+))?");
                if (match.Success)
                {
                    var h = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var m = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var sec = double.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                    var frac = match.Groups[4].Success ? double.Parse("0." + match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    TranscodedSeconds = h * 3600 + m * 60 + sec + frac;
                    TranscodeProgressChanged?.Invoke(this, TranscodedSeconds);
                }
            }
        };

        _ffmpegProcess.Start();
        _ffmpegProcess.BeginErrorReadLine();
        NocturneLogger.Info("WatchPartyService", $"FFmpeg transcoder spawned with pid={_ffmpegProcess.Id}");
    }

    private void StartTunnel(int localPort, TaskCompletionSource<string> tcs)
    {
        var cfExe = _binaryManager.GetCloudflaredPath();
        var psi = new ProcessStartInfo
        {
            FileName = cfExe,
            Arguments = $"tunnel --url http://127.0.0.1:{localPort}",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _tunnelProcess = new Process { StartInfo = psi };
        void ProcessLine(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            var match = Regex.Match(line, @"https://[a-zA-Z0-9-]+\.trycloudflare\.com");
            if (match.Success && !tcs.Task.IsCompleted)
            {
                var url = match.Value;
                NocturneLogger.Info("WatchPartyService", $"Cloudflared tunnel established: {url}");
                tcs.TrySetResult(url);
            }
        }

        _tunnelProcess.OutputDataReceived += (s, e) => { if (e.Data != null) ProcessLine(e.Data); };
        _tunnelProcess.ErrorDataReceived += (s, e) => { if (e.Data != null) ProcessLine(e.Data); };

        _tunnelProcess.Start();
        _tunnelProcess.BeginOutputReadLine();
        _tunnelProcess.BeginErrorReadLine();
        NocturneLogger.Info("WatchPartyService", $"Cloudflared tunnel spawned with pid={_tunnelProcess.Id}");
    }

    private async Task RunHttpServerAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = ProcessHttpRequestAsync(ctx);
        }
    }

    private async Task ProcessHttpRequestAsync(HttpListenerContext ctx)
    {
        try
        {
            ctx.Response.Headers.Add("Access-Control-Allow-Origin", "*");
            ctx.Response.Headers.Add("Access-Control-Allow-Methods", "GET, HEAD, OPTIONS");
            ctx.Response.Headers.Add("Access-Control-Allow-Headers", "*");

            if (ctx.Request.HttpMethod == "OPTIONS")
            {
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/ws")
            {
                var wsContext = await ctx.AcceptWebSocketAsync(subProtocol: null);
                _ = HandleWebSocketClientAsync(wsContext.WebSocket);
                return;
            }

            var path = ctx.Request.Url?.AbsolutePath?.TrimStart('/') ?? "";
            if (string.IsNullOrEmpty(path) || path == "index.html")
            {
                var bytes = Encoding.UTF8.GetBytes(WatchPartyGuestPage.Html);
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
                return;
            }

            if (path == "stream.m3u8" || Regex.IsMatch(path, @"^segment_\d{5}\.ts$"))
            {
                var filePath = Path.Combine(_sessionDir ?? "", path);

                // Wait up to 3 seconds for ffmpeg to finish flushing segment/playlist
                var retries = 0;
                while (!File.Exists(filePath) && retries < 15 && IsActive)
                {
                    await Task.Delay(200);
                    retries++;
                }

                if (File.Exists(filePath))
                {
                    ctx.Response.ContentType = path.EndsWith(".m3u8") ? "application/vnd.apple.mpegurl" : "video/mp2t";
                    ctx.Response.Headers.Add("Cache-Control", "no-cache");
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    ctx.Response.ContentLength64 = fs.Length;
                    if (ctx.Request.HttpMethod != "HEAD")
                    {
                        await fs.CopyToAsync(ctx.Response.OutputStream);
                    }
                    ctx.Response.Close();
                    return;
                }
            }

            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private async Task HandleWebSocketClientAsync(WebSocket ws)
    {
        lock (_wsClients) _wsClients.Add(ws);
        GuestCount = _wsClients.Count;
        GuestCountChanged?.Invoke(this, GuestCount);

        try
        {
            // Send session info
            var info = JsonSerializer.Serialize(new
            {
                type = "session_info",
                title = Title,
                durationSec = DurationSec,
                sessionStartedAt = StartedAtMs,
                startOffsetSec = StartOffsetSec
            });
            await SendToSocketAsync(ws, info);

            // Send current state
            var state = JsonSerializer.Serialize(new
            {
                type = "state",
                state = State == WatchPartySessionState.Live ? "LIVE" : "WAITING",
                position = CurrentPosition,
                playing = IsPlaying,
                serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            await SendToSocketAsync(ws, state);

            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                    break;
                }
            }
        }
        catch { }
        finally
        {
            lock (_wsClients) _wsClients.Remove(ws);
            try { ws.Dispose(); } catch { }
            GuestCount = _wsClients.Count;
            GuestCountChanged?.Invoke(this, GuestCount);
        }
    }

    private async Task BroadcastAsync(object message)
    {
        var json = JsonSerializer.Serialize(message);
        List<WebSocket> clients;
        lock (_wsClients)
        {
            clients = _wsClients.Where(c => c.State == WebSocketState.Open).ToList();
        }

        foreach (var client in clients)
        {
            try
            {
                await SendToSocketAsync(client, json);
            }
            catch { }
        }
    }

    private static async Task SendToSocketAsync(WebSocket ws, string json)
    {
        if (ws.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ = StopSessionAsync();
    }
}
