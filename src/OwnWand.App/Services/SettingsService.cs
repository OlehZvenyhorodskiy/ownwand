using System.IO;
using System.Text.Json;
using OwnWand.Core.Models;

namespace OwnWand.App.Services;

public class SettingsService
{
    private readonly string _settingsFilePath;
    private AppSettings _settings;

    public AppSettings Settings => _settings;

    public SettingsService()
    {
        var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OwnWand");
        Directory.CreateDirectory(appDataFolder);
        _settingsFilePath = Path.Combine(appDataFolder, "settings.json");
        _settings = LoadSettings();
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsFilePath))
            {
                var json = File.ReadAllText(_settingsFilePath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                if (settings != null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // Fallback to default
        }

        var defaultSettings = new AppSettings();
        SaveSettings(defaultSettings);
        return defaultSettings;
    }

    public void Save()
    {
        SaveSettings(_settings);
    }

    private void SaveSettings(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
            _settings = settings;
        }
        catch
        {
            // Ignored
        }
    }

    public void AddCustomGame(GameProfile profile)
    {
        if (_settings.CustomGames.Any(cg => cg.Id == profile.Id))
            return;

        _settings.CustomGames.Add(new CustomGameEntry
        {
            Id = profile.Id,
            Name = profile.Name,
            ExePath = profile.CustomExePath ?? string.Empty,
            ProcessName = profile.ProcessName
        });
        Save();
    }

    public void RemoveCustomGame(string id)
    {
        var game = _settings.CustomGames.FirstOrDefault(cg => cg.Id == id);
        if (game != null)
        {
            _settings.CustomGames.Remove(game);
            Save();
        }
    }

    public void SetCustomExePath(string gameId, string exePath)
    {
        _settings.GameExePaths[gameId] = exePath;
        Save();
    }

    public void RemoveCustomExePath(string gameId)
    {
        if (_settings.GameExePaths.Remove(gameId))
        {
            Save();
        }
    }
}
