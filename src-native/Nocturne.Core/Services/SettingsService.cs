using System.Text.Json;
using Nocturne.Core.Models;
using Nocturne.Core.Utils;

namespace Nocturne.Core.Services;

public class SettingsService
{
    public const string SettingsFileName = "nocturne-v4-settings.json";
    private const string SharedLegacyFileName = "nocturne-settings.json";

    private readonly string _settingsPath;
    private NocturneSettings _settings;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public event Action<NocturneSettings>? SettingsChanged;

    // Set when the settings file exists but could not be read. Nothing is saved over it until the user accepts
    // starting from defaults (AcceptDefaults), which moves the unreadable file aside first.
    public bool LoadFailed { get; private set; }
    public string? LoadError { get; private set; }
    public string SettingsPath => _settingsPath;

    public SettingsService() : this(ResolveDefaultPath(AppPaths.DataDirectory))
    {
    }

    public SettingsService(string settingsPath)
    {
        var dir = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        _settingsPath = settingsPath;
        _settings = Load();
    }

    public NocturneSettings Current => _settings;

    // v3.5 reads nocturne-settings.json as camelCase; v4 wrote PascalCase into the same file and dropped v3.5's
    // keys on every save. v4 now has its own file. A shared file in v4's format ("Servers", case-sensitive) is
    // copied over once; so is one that can't be parsed, so the load below reports it instead of starting blank.
    public static string ResolveDefaultPath(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, SettingsFileName);
        var shared = Path.Combine(dir, SharedLegacyFileName);
        if (File.Exists(path) || !File.Exists(shared)) return path;

        bool isV4;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(shared));
            isV4 = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("Servers", out _);
        }
        catch (Exception ex)
        {
            NocturneLogger.Warn("SettingsService", $"Shared settings file {shared} is unreadable ({ex.Message}); copying it so the load reports it.");
            isV4 = true;
        }

        if (isV4)
        {
            File.Copy(shared, path);
            NocturneLogger.Info("SettingsService", $"Copied v4 settings from {shared} to {path}; the shared file is left for v3.5.");
        }
        return path;
    }

    private NocturneSettings Load()
    {
        if (!File.Exists(_settingsPath)) return new NocturneSettings();
        try
        {
            return JsonSerializer.Deserialize<NocturneSettings>(File.ReadAllText(_settingsPath))
                ?? throw new JsonException("the file deserialized to null");
        }
        catch (Exception ex)
        {
            LoadFailed = true;
            LoadError = ex.Message;
            NocturneLogger.Error("SettingsService", $"Could not load {_settingsPath}; running on defaults and not saving over it.", ex);
            return new NocturneSettings();
        }
    }

    // The user chose to start from defaults: keep the unreadable file under a new name, then allow saves.
    public string AcceptDefaults()
    {
        var aside = $"{_settingsPath}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(_settingsPath, aside);
        LoadFailed = false;
        NocturneLogger.Warn("SettingsService", $"Unreadable settings moved to {aside}; starting from defaults.");
        return aside;
    }

    public void Save()
    {
        if (LoadFailed)
        {
            NocturneLogger.Warn("SettingsService", $"Not saving: {_settingsPath} failed to load and is left as it was.");
        }
        else
        {
            try
            {
                // Write-then-rename: a crash mid-write leaves the previous file, not a truncated one.
                var tmp = _settingsPath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_settings, _jsonOptions));
                File.Move(tmp, _settingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                NocturneLogger.Error("SettingsService", $"Failed to save {_settingsPath}", ex);
            }
        }
        SettingsChanged?.Invoke(_settings);
    }

    public void Update(Action<NocturneSettings> updateAction)
    {
        updateAction(_settings);
        Save();
    }
}
