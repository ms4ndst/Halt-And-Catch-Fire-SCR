using System.Text.Json;
using HcfScreensaver.Models;

namespace HcfScreensaver.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _settingsPath;

    public SettingsService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var directory = Path.Combine(appData, "HcfScreensaver");
        Directory.CreateDirectory(directory);
        _settingsPath = Path.Combine(directory, "settings.json");
    }

    public ScreensaverSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new ScreensaverSettings();

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<ScreensaverSettings>(json, JsonOptions)
                   ?? new ScreensaverSettings();
        }
        catch
        {
            return new ScreensaverSettings();
        }
    }

    public void Save(ScreensaverSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }
}
