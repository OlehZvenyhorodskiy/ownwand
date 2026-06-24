using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using OwnWand.App.Services;
using OwnWand.Core.Helpers;
using OwnWand.Core.Models;

namespace OwnWand.App.ViewModels;

public partial class GameLibraryViewModel : ObservableObject
{
    private readonly PresetService _presetService;
    private readonly SettingsService _settingsService;
    private readonly List<GameProfile> _allGames = new();

    [ObservableProperty]
    private ObservableCollection<GameProfile> _games = new();

    public GameLibraryViewModel(PresetService presetService, SettingsService settingsService)
    {
        _presetService = presetService;
        _settingsService = settingsService;
    }

    public async Task LoadGamesAsync()
    {
        _allGames.Clear();

        // Load built-in game presets
        var presets = await Task.Run(() => _presetService.LoadPresets());
        foreach (var game in presets)
        {
            if (_settingsService.Settings.GameExePaths.TryGetValue(game.Id, out var customPath))
            {
                game.CustomExePath = customPath;
                game.GameDirectory = Path.GetDirectoryName(customPath);
                var customProcessName = Path.GetFileNameWithoutExtension(customPath);
                if (!string.IsNullOrEmpty(customProcessName))
                {
                    game.ProcessName = customProcessName;
                }
                if (game.Runtime == UnityRuntime.Unknown && !string.IsNullOrEmpty(game.GameDirectory))
                {
                    game.Runtime = RuntimeDetector.DetectFromDirectory(game.GameDirectory);
                }
            }
        }
        _allGames.AddRange(presets);

        // Load custom games from settings
        foreach (var custom in _settingsService.Settings.CustomGames)
        {
            var gameDir = Path.GetDirectoryName(custom.ExePath) ?? string.Empty;
            var runtime = RuntimeDetector.DetectFromDirectory(gameDir);
            _allGames.Add(new GameProfile
            {
                Id = custom.Id,
                Name = custom.Name,
                ProcessName = custom.ProcessName,
                CustomExePath = custom.ExePath,
                GameDirectory = gameDir,
                Runtime = runtime,
                IsCustomGame = true,
                Features = _presetService.GetGenericFeatures()
            });
        }

        FilterGames(string.Empty);
    }

    public void FilterGames(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            Games = new ObservableCollection<GameProfile>(_allGames.OrderBy(g => g.Name));
        }
        else
        {
            var filtered = _allGames
                .Where(g => g.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            g.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(g => g.Name);
            Games = new ObservableCollection<GameProfile>(filtered);
        }
    }

    public void AddGame(GameProfile game)
    {
        if (!_allGames.Any(g => g.Id == game.Id))
        {
            _allGames.Add(game);
            FilterGames(string.Empty);
        }
    }

    public void RemoveGame(string id)
    {
        var game = _allGames.FirstOrDefault(g => g.Id == id);
        if (game != null)
        {
            _allGames.Remove(game);
            FilterGames(string.Empty);
        }
    }
}
