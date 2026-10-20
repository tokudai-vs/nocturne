using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class MpvController : IDisposable
{
    private Process? _mpvProcess;
    private NamedPipeClientStream? _pipeClient;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private readonly string _pipeName;
    private bool _isReady;
    private int _reqId;
    private CancellationTokenSource? _readCts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public event Action? PlaybackStarted;
    public event Action? PlaybackEnded;
    public event Action<double>? PositionChanged;
    public event Action<string>? ErrorOccurred;
    public event Action? NextEpisodeRequested;
    public event Action? PreviousEpisodeRequested;
    public event Action<double>? DurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<double>? VolumeChanged;
    public event Action<int>? SubtitleTrackChanged;
    public event Action<int>? AudioTrackChanged;

    public MpvController()
    {
        _pipeName = $"nocturne-mpv-{Environment.ProcessId}";
    }

    public bool IsPlaying { get; private set; }
    public bool IsPaused { get; private set; }
    public double CurrentVolume { get; private set; } = 100;
    public double CurrentPositionSeconds { get; private set; }
    public double DurationSeconds { get; private set; }
    public int CurrentSubtitleTrack { get; private set; }
    public int CurrentAudioTrack { get; private set; }

    public async Task StartIdleAsync()
    {
        if (_mpvProcess != null && !_mpvProcess.HasExited) return;

        var mpvExe = FindMpvExecutable();
        if (string.IsNullOrEmpty(mpvExe) || !File.Exists(mpvExe))
        {
            NocturneLogger.Error("MpvController", $"mpv.exe not found at '{mpvExe}'");
            ErrorOccurred?.Invoke($"mpv.exe not found at '{mpvExe}'");
            return;
        }

        var mpvDir = Path.GetDirectoryName(mpvExe)!;
        var configDir = Path.Combine(mpvDir, "portable_config");

        var args = new List<string>
        {
            "--idle=yes",
            $"--input-ipc-server=\\\\.\\pipe\\{_pipeName}",
            "--force-window=no",
            "--osc=yes",
            "--really-quiet"
        };

        if (Directory.Exists(configDir))
        {
            args.Add($"--config-dir=\"{configDir}\"");
        }

        var psi = new ProcessStartInfo
        {
            FileName = mpvExe,
            Arguments = string.Join(" ", args),
            CreateNoWindow = false,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Normal
        };

        try
        {
            _mpvProcess = Process.Start(psi);
            if (_mpvProcess == null)
            {
                ErrorOccurred?.Invoke("Failed to spawn mpv process.");
                return;
            }

            _mpvProcess.EnableRaisingEvents = true;
            _mpvProcess.Exited += (s, e) =>
            {
                IsPlaying = false;
                PlaybackEnded?.Invoke();
            };

            await ConnectPipeAsync();
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("MpvController", "Failed to start mpv", ex);
            ErrorOccurred?.Invoke($"Failed to start mpv: {ex.Message}");
        }
    }

    private async Task ConnectPipeAsync()
    {
        _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        var attempts = 0;
        while (attempts < 25)
        {
            try
            {
                if (_pipeClient == null) break;
                await _pipeClient.ConnectAsync(400);
                if (_pipeClient != null && _pipeClient.IsConnected) break;
            }
            catch
            {
                await Task.Delay(100);
            }
            attempts++;
        }

        if (_pipeClient == null || !_pipeClient.IsConnected)
        {
            ErrorOccurred?.Invoke("Failed to connect to mpv named pipe.");
            return;
        }

        _writer = new StreamWriter(_pipeClient, Encoding.UTF8, 1024, leaveOpen: true) { AutoFlush = true };
        _reader = new StreamReader(_pipeClient, Encoding.UTF8, false, 1024, leaveOpen: true);
        _isReady = true;

        _readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));

        // Observe key properties
        await SendCommandAsync(new object[] { "observe_property", 1, "time-pos" });
        await SendCommandAsync(new object[] { "observe_property", 2, "duration" });
        await SendCommandAsync(new object[] { "observe_property", 3, "eof-reached" });
        await SendCommandAsync(new object[] { "observe_property", 4, "pause" });
        await SendCommandAsync(new object[] { "observe_property", 5, "volume" });
        await SendCommandAsync(new object[] { "observe_property", 6, "sid" });
        await SendCommandAsync(new object[] { "observe_property", 7, "aid" });
    }

    public async Task PlayEmbeddedAsync(
        IntPtr hwnd,
        string url,
        string title,
        long startPositionTicks = 0,
        string? subFont = null,
        int? subSize = null,
        string? subColor = null,
        string? subBorder = null,
        string? subBg = null)
    {
        await StopAsync(notifyEnded: false);

        var mpvExe = FindMpvExecutable();
        if (string.IsNullOrEmpty(mpvExe) || !File.Exists(mpvExe))
        {
            NocturneLogger.Error("MpvController", $"mpv.exe not found at '{mpvExe}'");
            ErrorOccurred?.Invoke($"mpv.exe not found at '{mpvExe}'");
            return;
        }

        var mpvDir = Path.GetDirectoryName(mpvExe)!;
        var configDir = Path.Combine(mpvDir, "portable_config");

        double borderSize = subBorder?.ToLowerInvariant() switch
        {
            "thin" => 1.5,
            "medium" => 3.0,
            "thick" => 4.5,
            _ => 0.0 // "none" by default
        };

        var args = new List<string>
        {
            $"--wid={(long)hwnd}",
            $"--input-ipc-server=\\\\.\\pipe\\{_pipeName}",
            "--osc=no",
            "--really-quiet",
            "--keep-open=yes",
            "--input-default-bindings=yes",
            "--input-vo-keyboard=yes",
            "--input-cursor=yes",
            "--force-window=yes",
            "--hwdec=auto",
            "--load-scripts=no",
            "--osd-level=0",
            "--osd-bar=no",
            "--border=no",
            "--sub-auto=all",
            "--sub-visibility=yes",
            $"--sub-border-size={borderSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrEmpty(subFont))
            args.Add($"--sub-font=\"{subFont}\"");
        if (subSize.HasValue && subSize.Value > 0)
            args.Add($"--sub-font-size={subSize.Value}");
        if (!string.IsNullOrEmpty(subColor))
            args.Add($"--sub-color=\"{subColor}\"");

        if (subBg == "semi")
            args.Add("--sub-back-color=\"#80000000\"");
        else if (subBg == "opaque")
            args.Add("--sub-back-color=\"#FF000000\"");

        if (Directory.Exists(configDir))
        {
            args.Add($"--config-dir=\"{configDir}\"");
        }

        var psi = new ProcessStartInfo
        {
            FileName = mpvExe,
            Arguments = string.Join(" ", args),
            CreateNoWindow = true,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            _mpvProcess = Process.Start(psi);
            if (_mpvProcess == null)
            {
                ErrorOccurred?.Invoke("Failed to spawn mpv process.");
                return;
            }

            _mpvProcess.EnableRaisingEvents = true;
            _mpvProcess.Exited += (s, e) =>
            {
                if (IsPlaying)
                {
                    IsPlaying = false;
                    PlaybackEnded?.Invoke();
                }
            };

            await ConnectPipeAsync();

            if (startPositionTicks > 0)
            {
                var startSeconds = (double)startPositionTicks / 10_000_000.0;
                await SendCommandAsync(new object[] { "set_property", "start", startSeconds });
            }
            else
            {
                await SendCommandAsync(new object[] { "set_property", "start", 0 });
            }

            await SendCommandAsync(new object[] { "set_property", "force-media-title", title });
            await SendCommandAsync(new object[] { "loadfile", url, "replace" });

            IsPlaying = true;
            PlaybackStarted?.Invoke();
            NocturneLogger.Info("MpvController", $"Embedded playback started: {title} ({url}) in HWND {hwnd}");
        }
        catch (Exception ex)
        {
            NocturneLogger.Error("MpvController", "Failed to start embedded mpv", ex);
            ErrorOccurred?.Invoke($"Failed to start embedded mpv: {ex.Message}");
        }
    }

    public async Task PlayAsync(string url, string title, long startPositionTicks = 0)
    {
        if (!_isReady || _writer == null)
        {
            await StartIdleAsync();
        }

        if (startPositionTicks > 0)
        {
            var startSeconds = (double)startPositionTicks / 10_000_000.0;
            await SendCommandAsync(new object[] { "set_property", "start", startSeconds });
        }
        else
        {
            await SendCommandAsync(new object[] { "set_property", "start", 0 });
        }

        await SendCommandAsync(new object[] { "set_property", "force-media-title", title });
        await SendCommandAsync(new object[] { "set_property", "force-window", "yes" });
        await SendCommandAsync(new object[] { "set_property", "fullscreen", "yes" });
        await SendCommandAsync(new object[] { "set_property", "ontop", "yes" });
        await SendCommandAsync(new object[] { "loadfile", url, "replace" });

        IsPlaying = true;
        PlaybackStarted?.Invoke();
        NocturneLogger.Info("MpvController", $"Playback started: {title} ({url})");
    }

    public async Task PauseAsync()
    {
        await SendCommandAsync(new object[] { "cycle", "pause" });
    }

    public async Task SetPauseAsync(bool pause)
    {
        IsPaused = pause;
        await SendCommandAsync(new object[] { "set_property", "pause", pause });
    }

    public async Task TogglePauseAsync()
    {
        await SendCommandAsync(new object[] { "cycle", "pause" });
    }

    public async Task SetVolumeAsync(double volume)
    {
        CurrentVolume = Math.Clamp(volume, 0, 100);
        await SendCommandAsync(new object[] { "set_property", "volume", CurrentVolume });
    }

    public async Task StopAsync(bool notifyEnded = true)
    {
        var wasPlaying = IsPlaying;
        if (_writer != null && _isReady)
        {
            try
            {
                await SendCommandAsync(new object[] { "quit" });
            }
            catch { }
        }

        try { _readCts?.Cancel(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        try { _reader?.Dispose(); } catch { }
        _reader = null;
        try { _pipeClient?.Dispose(); } catch { }
        _pipeClient = null;
        _isReady = false;

        if (_mpvProcess != null && !_mpvProcess.HasExited)
        {
            try
            {
                if (!_mpvProcess.WaitForExit(350))
                {
                    _mpvProcess.Kill();
                }
            }
            catch { }
            try { _mpvProcess.Dispose(); } catch { }
            _mpvProcess = null;
        }

        IsPlaying = false;
        if (notifyEnded && wasPlaying)
        {
            try { PlaybackEnded?.Invoke(); } catch { }
        }
    }

    public async Task SeekTo(double absoluteSeconds)
    {
        await SendCommandAsync(new object[] { "seek", absoluteSeconds, "absolute" });
    }

    public async Task SeekRelative(double seconds)
    {
        await SendCommandAsync(new object[] { "seek", seconds, "relative" });
    }

    public async Task CycleSubtitles()
    {
        await SendCommandAsync(new object[] { "cycle", "sub" });
    }

    public async Task CycleAudio()
    {
        await SendCommandAsync(new object[] { "cycle", "audio" });
    }

    public async Task SetSubtitleTrack(int trackId)
    {
        if (trackId <= 0)
        {
            await SendCommandAsync(new object[] { "set_property", "sid", "no" });
            await SendCommandAsync(new object[] { "set_property", "sub-visibility", false });
            await ShowOsdText("Subtitles: Off");
        }
        else
        {
            await SendCommandAsync(new object[] { "set_property", "sub-visibility", true });
            await SendCommandAsync(new object[] { "set_property", "sid", trackId });
        }
    }

    public async Task SetSubtitleBorderSizeAsync(double borderSize)
    {
        await SendCommandAsync(new object[] { "set_property", "sub-border-size", borderSize });
    }

    public async Task SetSubtitleStyleAsync(string? font, int? size, string? color, string? border, string? background)
    {
        if (!string.IsNullOrEmpty(font))
            await SendCommandAsync(new object[] { "set_property", "sub-font", font });
        if (size.HasValue && size.Value > 0)
            await SendCommandAsync(new object[] { "set_property", "sub-font-size", size.Value });
        if (!string.IsNullOrEmpty(color))
            await SendCommandAsync(new object[] { "set_property", "sub-color", color });

        double borderSize = border?.ToLowerInvariant() switch
        {
            "thin" => 1.5,
            "medium" => 3.0,
            "thick" => 4.5,
            _ => 0.0
        };
        await SendCommandAsync(new object[] { "set_property", "sub-border-size", borderSize });

        if (background == "semi")
            await SendCommandAsync(new object[] { "set_property", "sub-back-color", "#80000000" });
        else if (background == "opaque")
            await SendCommandAsync(new object[] { "set_property", "sub-back-color", "#FF000000" });
        else
            await SendCommandAsync(new object[] { "set_property", "sub-back-color", "#00000000" });
    }

    public async Task SetAudioTrack(int trackId)
    {
        await SendCommandAsync(new object[] { "set_property", "aid", trackId });
    }

    public async Task ShowOsdText(string text, int durationMs = 2000)
    {
        await SendCommandAsync(new object[] { "show-text", text, durationMs });
    }

    public void TriggerNextEpisode() => NextEpisodeRequested?.Invoke();
    public void TriggerPreviousEpisode() => PreviousEpisodeRequested?.Invoke();

    private async Task SendCommandAsync(object[] command)
    {
        if (_writer == null || !_isReady) return;
        var req = new
        {
            command = command,
            request_id = Interlocked.Increment(ref _reqId)
        };
        var json = JsonSerializer.Serialize(req);
        try
        {
            if (!await _writeLock.WaitAsync(500)) return;
        }
        catch { return; }

        try
        {
            if (_writer != null && _isReady)
            {
                await _writer.WriteLineAsync(json);
            }
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("MpvController", $"Send error: {ex.Message}");
        }
        finally
        {
            try { _writeLock.Release(); } catch { }
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _reader != null)
        {
            try
            {
                var line = await _reader.ReadLineAsync(ct);
                if (string.IsNullOrEmpty(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (root.TryGetProperty("event", out var evProp))
                {
                    var ev = evProp.GetString();
                    if (ev == "property-change" && root.TryGetProperty("name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (name == "time-pos" && root.TryGetProperty("data", out var dataProp))
                        {
                            if (dataProp.ValueKind == JsonValueKind.Number)
                            {
                                CurrentPositionSeconds = dataProp.GetDouble();
                                PositionChanged?.Invoke(CurrentPositionSeconds);
                            }
                        }
                        else if (name == "duration" && root.TryGetProperty("data", out var durProp))
                        {
                            if (durProp.ValueKind == JsonValueKind.Number)
                            {
                                DurationSeconds = durProp.GetDouble();
                                DurationChanged?.Invoke(DurationSeconds);
                            }
                        }
                        else if (name == "pause" && root.TryGetProperty("data", out var pauseProp))
                        {
                            IsPaused = pauseProp.ValueKind == JsonValueKind.True;
                            PauseChanged?.Invoke(IsPaused);
                        }
                        else if (name == "volume" && root.TryGetProperty("data", out var volProp))
                        {
                            if (volProp.ValueKind == JsonValueKind.Number)
                            {
                                CurrentVolume = volProp.GetDouble();
                                VolumeChanged?.Invoke(CurrentVolume);
                            }
                        }
                        else if (name == "eof-reached" && root.TryGetProperty("data", out var eofProp))
                        {
                            if (eofProp.ValueKind == JsonValueKind.True)
                            {
                                NocturneLogger.Info("MpvController", "EOF reached. Invoking next episode / completion.");
                                NextEpisodeRequested?.Invoke();
                            }
                        }
                        else if (name == "sid" && root.TryGetProperty("data", out var sidProp))
                        {
                            if (sidProp.ValueKind == JsonValueKind.Number)
                            {
                                CurrentSubtitleTrack = sidProp.GetInt32();
                                SubtitleTrackChanged?.Invoke(CurrentSubtitleTrack);
                            }
                            else if (sidProp.ValueKind == JsonValueKind.False || (sidProp.ValueKind == JsonValueKind.String && sidProp.GetString() == "no"))
                            {
                                CurrentSubtitleTrack = 0;
                                SubtitleTrackChanged?.Invoke(0);
                            }
                        }
                        else if (name == "aid" && root.TryGetProperty("data", out var aidProp))
                        {
                            if (aidProp.ValueKind == JsonValueKind.Number)
                            {
                                CurrentAudioTrack = aidProp.GetInt32();
                                AudioTrackChanged?.Invoke(CurrentAudioTrack);
                            }
                        }
                    }
                    else if (ev == "end-file")
                    {
                        _ = SendCommandAsync(new object[] { "set_property", "fullscreen", "no" });
                        _ = SendCommandAsync(new object[] { "set_property", "force-window", "no" });
                        _ = SendCommandAsync(new object[] { "set_property", "ontop", "no" });
                        IsPlaying = false;
                        PlaybackEnded?.Invoke();
                    }
                }
                else if (root.TryGetProperty("error", out var errProp))
                {
                    var err = errProp.GetString();
                    if (!string.IsNullOrEmpty(err) && err != "success")
                    {
                        NocturneLogger.Warn("MpvController", $"mpv IPC response: {line}");
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (IOException ex)
            {
                NocturneLogger.Info("MpvController", $"Pipe disconnected: {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                NocturneLogger.Warn("MpvController", $"Read error: {ex.Message}");
                break;
            }
        }
    }

    private static string FindMpvExecutable()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var localMpv = Path.Combine(baseDir, "resources", "mpv", "mpv.exe");
        if (File.Exists(localMpv)) return localMpv;

        var siblingMpv = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "resources", "mpv", "mpv.exe"));
        if (File.Exists(siblingMpv)) return siblingMpv;

        return "mpv.exe";
    }

    public void Dispose()
    {
        try { _readCts?.Cancel(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        try { _reader?.Dispose(); } catch { }
        _reader = null;
        try { _pipeClient?.Dispose(); } catch { }
        _pipeClient = null;
        if (_mpvProcess != null && !_mpvProcess.HasExited)
        {
            try { _mpvProcess.Kill(); } catch { }
            try { _mpvProcess.Dispose(); } catch { }
            _mpvProcess = null;
        }
    }
}
