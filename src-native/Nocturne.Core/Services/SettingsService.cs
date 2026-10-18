using System.Text.Json;
using Nocturne.Core.Models;

namespace Nocturne.Core.Services;

public class SettingsService
{
    private readonly string _settingsPath;
    private NocturneSettings _settings;
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    public event Action<NocturneSettings>? SettingsChanged;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "Nocturne");
        Directory.CreateDirectory(dir);
        _settingsPath = Path.Combine(dir, "nocturne-settings.json");
        _settings = Load();
    }

    public NocturneSettings Current => _settings;

    private NocturneSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                var loaded = JsonSerializer.Deserialize<NocturneSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to load: {ex.Message}");
        }
        return new NocturneSettings();
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_settings, _jsonOptions);
            File.WriteAllText(_settingsPath, json);
            SettingsChanged?.Invoke(_settings);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Settings] Failed to save: {ex.Message}");
        }
    }

    public void Update(Action<NocturneSettings> updateAction)
    {
        updateAction(_settings);
        Save();
    }
}
