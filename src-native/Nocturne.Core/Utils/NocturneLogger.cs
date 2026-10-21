using System.IO;

namespace Nocturne.Core.Utils;

public static class NocturneLogger
{
    private static readonly object _lock = new();
    private static readonly string _logPath;

    static NocturneLogger()
    {
        try
        {
            var dir = AppPaths.DataDirectory;
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "nocturne.log");
        }
        catch
        {
            _logPath = "nocturne.log";
        }
    }

    public static void Info(string category, string message) => Log("INFO", category, message);
    public static void Warn(string category, string message) => Log("WARN", category, message);
    public static void Error(string category, string message, Exception? ex = null)
    {
        var msg = ex != null ? $"{message} (Exception: {ex.Message})\n{ex.StackTrace}" : message;
        Log("ERROR", category, msg);
    }

    private static void Log(string level, string category, string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [{category}] {message}";
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }
        catch { }

        Console.WriteLine(line);
    }
}
