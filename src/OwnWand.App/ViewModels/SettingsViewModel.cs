using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnWand.App.Services;
using OwnWand.Core.Models;

namespace OwnWand.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly GameLibraryViewModel _gameLibraryViewModel;

    [ObservableProperty]
    private bool _overlayEnabled;

    [ObservableProperty]
    private bool _minimizeToTray;

    [ObservableProperty]
    private bool _autoDetectGames;

    [ObservableProperty]
    private int _gameScanIntervalSeconds;

    [ObservableProperty]
    private string _theme = "Dark";

    [ObservableProperty]
    private ObservableCollection<CustomGameEntry> _customGames = new();

    public SettingsViewModel(SettingsService settingsService, GameLibraryViewModel gameLibraryViewModel)
    {
        _settingsService = settingsService;
        _gameLibraryViewModel = gameLibraryViewModel;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var s = _settingsService.Settings;
        OverlayEnabled = s.OverlayEnabled;
        MinimizeToTray = s.MinimizeToTray;
        AutoDetectGames = s.AutoDetectGames;
        GameScanIntervalSeconds = s.GameScanIntervalSeconds;
        Theme = s.Theme;
        CustomGames = new ObservableCollection<CustomGameEntry>(s.CustomGames);
    }

    partial void OnOverlayEnabledChanged(bool value)
    {
        _settingsService.Settings.OverlayEnabled = value;
        _settingsService.Save();
    }

    partial void OnMinimizeToTrayChanged(bool value)
    {
        _settingsService.Settings.MinimizeToTray = value;
        _settingsService.Save();
    }

    partial void OnAutoDetectGamesChanged(bool value)
    {
        _settingsService.Settings.AutoDetectGames = value;
        _settingsService.Save();
    }

    partial void OnGameScanIntervalSecondsChanged(int value)
    {
        _settingsService.Settings.GameScanIntervalSeconds = value;
        _settingsService.Save();
    }

    partial void OnThemeChanged(string value)
    {
        _settingsService.Settings.Theme = value;
        _settingsService.Save();
        // Theme switching logic can go here if needed
    }

    [RelayCommand]
    private void RemoveCustomGame(string gameId)
    {
        _settingsService.RemoveCustomGame(gameId);
        _gameLibraryViewModel.RemoveGame(gameId);
        
        var existing = CustomGames.FirstOrDefault(g => g.Id == gameId);
        if (existing != null)
        {
            CustomGames.Remove(existing);
        }
    }
}
