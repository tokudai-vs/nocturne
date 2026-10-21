using System.IO;
using System.Text.RegularExpressions;

namespace Nocturne.Core.Utils;

public static class NocturneLogger
{
    private static readonly object _lock = new();
    private static string? _logPath;
    private static long _size = -1;

    // Past this the log is moved to nocturne.log.1 (one generation kept) and a new one started.
    internal static long MaxLogBytes = 5 * 1024 * 1024;

    // Defaults to nocturne.log in AppPaths.DataDirectory. Tests set it to a temp file (Nocturne.Tests TestSetup)
    // so they never write into the user's log.
    public static string LogPath
    {
        get { lock (_lock) return _logPath ??= DefaultPath(); }
        set { lock (_lock) { _logPath = value; _size = -1; } }
    }

    // Credentials that reach log lines: Emby api_key in stream URLs, Emby/Trakt tokens, OAuth device codes and
    // PKCE verifiers, in key=value, "key":"value" and header forms. user_code stays readable (the user types it).
    private static readonly Regex Secrets = new(
        @"(?i)(api_key|access_?token|refresh_?token|device_code|code_verifier|client_secret|x-(?:emby|mediabrowser)-token|\btoken)(""?\s*[=:]\s*""?)([^&\s""',;}]+)",
        RegexOptions.Compiled);

    public static string Redact(string text) => Secrets.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + "<redacted>");

    public static void Info(string category, string message) => Log("INFO", category, message);
    public static void Warn(string category, string message) => Log("WARN", category, message);
    public static void Error(string category, string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message} (Exception: {ex.Message})\n{ex.StackTrace}" : message;
        Log("ERROR", category, msg);
    }

    private static string DefaultPath()
    {
        try
        {
            var dir = AppPaths.DataDirectory;
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "nocturne.log");
        }
        catch
        {
            return "nocturne.log";
        }
    }

    private static void Log(string level, string category, string message)
    {
        var line = Redact($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}");
        try
        {
            lock (_lock)
            {
                var path = _logPath ??= DefaultPath();
                if (_size < 0) _size = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (_size > MaxLogBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                    _size = 0;
                }
                File.AppendAllText(path, line + Environment.NewLine);
                _size += line.Length + Environment.NewLine.Length;
            }
        }
        catch { }

        Console.WriteLine(line);
    }
}
