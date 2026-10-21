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
    // The (N+1)th guest is refused at the socket with a session_full frame and close code 1008 (v3.5's rule,
    // src/main/watchparty-sync-server.ts:86). Fixed, not a setting.
    public const int MaxGuests = 10;

    private readonly WatchPartyBinaryManager _binaryManager;
    private readonly List<WebSocket> _wsClients = new();
    private readonly Dictionary<WebSocket, double> _guestBehindSec = new();
    // A WebSocket allows one send at a time. Broadcasts are fire-and-forget from the mpv event thread and the
    // heartbeat timer; overlapping sends made the server abort the guest's connection (guests dropped and
    // reconnected every 15-25 s). One gate per socket serialises them.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WebSocket, SemaphoreSlim> SendGates = new();
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
    // Stream time: seconds into the HLS output, which starts at StartOffsetSec of the movie.
    public double CurrentPosition { get; private set; }
    // How far the slowest guest trails the host, from the positions guests report every 2 s. 0 with no reports.
    public double GuestsBehindSec { get; private set; }
    public bool IsPlaying { get; private set; }
    public string? ErrorMessage { get; private set; }

    public bool IsActive => State == WatchPartySessionState.Waiting || State == WatchPartySessionState.Live;

    public event EventHandler<WatchPartySessionState>? StateChanged;
    public event EventHandler<int>? GuestCountChanged;
    public event EventHandler<double>? TranscodeProgressChanged;
    public event EventHandler<string>? TunnelUrlResolved;
    public event EventHandler<double>? GuestsBehindChanged;

    public WatchPartyService(WatchPartyBinaryManager binaryManager)
    {
        _binaryManager = binaryManager;
    }

    public bool AreBinariesReady() => _binaryManager.IsReady();

    public Task<bool> EnsureBinariesAsync(IProgress<(string phase, int percent)>? progress = null, CancellationToken ct = default)
    {
        return _binaryManager.EnsureBinariesAsync(progress, ct);
    }

    // publicLink: false = local-only (http://127.0.0.1, this PC), no cloudflared tunnel.
    public Task<string> StartSessionAsync(
        string title,
        string streamUrl,
        double durationSec,
        double startOffsetSec = 0,
        int targetHeight = 1080,
        bool publicLink = true,
        CancellationToken ct = default)
        => StartSessionAsync(title, streamUrl, durationSec, startOffsetSec, targetHeight, publicLink, transcode: true, ct);

    // transcode: false serves only the guest page and sync socket (tests).
    internal async Task<string> StartSessionAsync(
        string title,
        string streamUrl,
        double durationSec,
        double startOffsetSec,
        int targetHeight,
        bool publicLink,
        bool transcode,
        CancellationToken ct)
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
            if ((transcode || publicLink) && !_binaryManager.IsReady())
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
            CurrentPosition = 0;
            GuestsBehindSec = 0;
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

            if (transcode) StartTranscoder(streamUrl, targetHeight, startOffsetSec);

            if (publicLink)
            {
                var tunnelTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                StartTunnel(port, tunnelTcs);

                // A 127.0.0.1 "share link" is useless to remote guests: no tunnel URL is a failed start.
                var completedTask = await Task.WhenAny(tunnelTcs.Task, Task.Delay(30000, ct));
                if (completedTask != tunnelTcs.Task)
                    throw new InvalidOperationException("The public link could not be opened: cloudflared reported no tunnel URL within 30 seconds.");
                TunnelUrl = await tunnelTcs.Task;
                TunnelUrlResolved?.Invoke(this, TunnelUrl);
            }

            State = WatchPartySessionState.Waiting;
            StateChanged?.Invoke(this, State);

            // 1-second heartbeat of the last known stream position
            _heartbeatTimer = new Timer(_ =>
            {
                if (IsActive && _wsClients.Count > 0)
                {
                    SendPosition("heartbeat");
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
            IsPlaying = true;
            SendPosition("play");
        }
    }

    // The host reports mpv's movie position. ffmpeg seeks with -ss before -i, so the HLS stream's t=0 is
    // StartOffsetSec of the movie: guests are sent stream time. Sending movie time put every guest of a party
    // started from a resume point StartOffsetSec ahead, past the transcoded part.
    internal double ToStreamTime(double moviePosition) => Math.Max(0, moviePosition - StartOffsetSec);

    public void BroadcastPlay(double moviePosition)
    {
        IsPlaying = true;
        CurrentPosition = ToStreamTime(moviePosition);
        SendPosition("play");
    }

    public void BroadcastPause(double moviePosition)
    {
        IsPlaying = false;
        CurrentPosition = ToStreamTime(moviePosition);
        SendPosition("pause");
    }

    public void BroadcastSeek(double moviePosition)
    {
        CurrentPosition = ToStreamTime(moviePosition);
        SendPosition("seek");
    }

    // Called on every mpv position event: records the position; the 1 s timer sends the heartbeat.
    public void BroadcastHeartbeat(double moviePosition, bool playing)
    {
        IsPlaying = playing;
        CurrentPosition = ToStreamTime(moviePosition);
    }

    private void SendPosition(string type)
    {
        _ = BroadcastAsync(new
        {
            type,
            position = CurrentPosition,
            serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    // ffmpeg goes first so it stops pulling from Emby whatever happens next; guests get session_end (bounded
    // wait) while the tunnel still carries it; then cloudflared and the server. ChildProcessJob covers a crash.
    public async Task StopSessionAsync()
    {
        if (State == WatchPartySessionState.Idle && _listener == null && _ffmpegProcess == null) return;

        State = WatchPartySessionState.Ended;
        StateChanged?.Invoke(this, State);

        _heartbeatTimer?.Dispose();
        _heartbeatTimer = null;

        Kill(ref _ffmpegProcess, graceful: true);

        try
        {
            await Task.WhenAny(BroadcastAsync(new { type = "session_end" }), Task.Delay(1500));
        }
        catch { }

        Kill(ref _tunnelProcess, graceful: false);

        // Snapshot, then dispose outside the lock: disposing runs the socket's receive-loop finally inline, which
        // removes it from _wsClients ("Collection was modified" stopped the teardown half-way).
        List<WebSocket> clients;
        lock (_wsClients)
        {
            clients = _wsClients.ToList();
            _wsClients.Clear();
            _guestBehindSec.Clear();
        }
        foreach (var ws in clients)
        {
            try { ws.Dispose(); } catch { }
        }
        GuestCount = 0;
        GuestCountChanged?.Invoke(this, 0);
        GuestsBehindSec = 0;

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;

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

    private static void Kill(ref Process? process, bool graceful)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                if (graceful)
                {
                    try { process.StandardInput.WriteLine("q"); } catch { }
                    if (!process.WaitForExit(1000)) process.Kill(entireProcessTree: true);
                }
                else
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
        catch
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
        process.Dispose();
        process = null;
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
        ChildProcessJob.Add(_ffmpegProcess);
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
        ChildProcessJob.Add(_tunnelProcess);
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
            if (path == "hls.min.js")
            {
                // Bundled (hls.js 1.6.16, Apache-2.0, the version v3.5 ships) instead of loading it from jsDelivr.
                using var js = typeof(WatchPartyService).Assembly.GetManifestResourceStream("hls.min.js")!;
                ctx.Response.ContentType = "application/javascript; charset=utf-8";
                ctx.Response.Headers.Add("Cache-Control", "max-age=86400");
                ctx.Response.ContentLength64 = js.Length;
                await js.CopyToAsync(ctx.Response.OutputStream);
                ctx.Response.Close();
                return;
            }

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
        bool admitted;
        lock (_wsClients)
        {
            admitted = _wsClients.Count < MaxGuests;
            if (admitted) _wsClients.Add(ws);
            GuestCount = _wsClients.Count;
        }
        if (!admitted)
        {
            NocturneLogger.Warn("WatchPartyService", $"Refusing a guest: the {MaxGuests}-guest cap is reached.");
            try
            {
                await SendToSocketAsync(ws, JsonSerializer.Serialize(new { type = "session_full" }));
                await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, "session_full", CancellationToken.None);
            }
            catch { }
            ws.Dispose();
            return;
        }
        GuestCountChanged?.Invoke(this, GuestCount);
        NocturneLogger.Info("WatchPartyService", $"Guest joined ({GuestCount}/{MaxGuests}).");

        try
        {
            // Send session info
            var info = JsonSerializer.Serialize(new
            {
                type = "session_info",
                title = Title,
                durationSec = DurationSec,
                sessionStartedAt = StartedAtMs
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
                if (result.MessageType == WebSocketMessageType.Text && result.EndOfMessage)
                    OnGuestMessage(ws, Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch { }
        finally
        {
            lock (_wsClients)
            {
                _wsClients.Remove(ws);
                _guestBehindSec.Remove(ws);
                GuestCount = _wsClients.Count;
            }
            try { ws.Dispose(); } catch { }
            GuestCountChanged?.Invoke(this, GuestCount);
            UpdateGuestsBehind();
            NocturneLogger.Info("WatchPartyService", $"Guest left ({GuestCount}/{MaxGuests}).");
        }
    }

    // Guests report {"type":"position","position":<stream seconds>} every 2 s while live.
    private void OnGuestMessage(WebSocket ws, string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "position"
                && root.TryGetProperty("position", out var pos) && pos.TryGetDouble(out var guestPos))
            {
                lock (_wsClients) _guestBehindSec[ws] = Math.Max(0, CurrentPosition - guestPos);
                UpdateGuestsBehind();
            }
        }
        catch (JsonException) { }
    }

    private void UpdateGuestsBehind()
    {
        double behind;
        lock (_wsClients) behind = _guestBehindSec.Count > 0 ? _guestBehindSec.Values.Max() : 0;
        if (Math.Abs(behind - GuestsBehindSec) < 0.5) return;
        GuestsBehindSec = behind;
        GuestsBehindChanged?.Invoke(this, behind);
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
        var gate = SendGates.GetValue(ws, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (ws.State != WebSocketState.Open) return;
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally
        {
            gate.Release();
        }
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // App exit: the helper processes are killed synchronously before anything else is attempted.
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill(ref _ffmpegProcess, graceful: false);
        Kill(ref _tunnelProcess, graceful: false);
        _ = StopSessionAsync();
    }
}
