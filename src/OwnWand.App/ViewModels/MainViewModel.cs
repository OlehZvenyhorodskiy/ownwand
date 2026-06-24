using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnWand.App.Services;
using OwnWand.Core.Models;

namespace OwnWand.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly PresetService _presetService;
    private readonly ProcessService _processService;
    private readonly GameDetectionService _gameDetectionService;
    private readonly InjectionService _injectionService;
    private readonly IpcService _ipcService;
    private readonly ProfileService _profileService;
    private readonly SettingsService _settingsService;
    private Views.TransparentEspOverlay? _espOverlay;

    [ObservableProperty]
    private GameLibraryViewModel _gameLibrary;

    [ObservableProperty]
    private CheatPanelViewModel? _cheatPanel;

    [ObservableProperty]
    private SettingsViewModel _settings;

    [ObservableProperty]
    private GameProfile? _selectedGame;

    [ObservableProperty]
    private AttachmentStatus _attachmentStatus = AttachmentStatus.Disconnected;

    [ObservableProperty]
    private string _statusText = "Not connected";

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private bool _isInitialized;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    public MainViewModel(
        PresetService presetService,
        ProcessService processService,
        GameDetectionService gameDetectionService,
        InjectionService injectionService,
        IpcService ipcService,
        ProfileService profileService,
        SettingsService settingsService,
        GameLibraryViewModel gameLibrary,
        SettingsViewModel settings)
    {
        _presetService = presetService;
        _processService = processService;
        _gameDetectionService = gameDetectionService;
        _injectionService = injectionService;
        _ipcService = ipcService;
        _profileService = profileService;
        _settingsService = settingsService;
        _gameLibrary = gameLibrary;
        _settings = settings;
    }

    public async Task InitializeAsync()
    {
        await GameLibrary.LoadGamesAsync();
        _gameDetectionService.StartScanning();
        _gameDetectionService.GameDetected += OnGameDetected;
        _gameDetectionService.GameLost += OnGameLost;
        IsInitialized = true;
    }

    private void OnGameDetected(object? sender, GameProfile game)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = GameLibrary.Games.FirstOrDefault(g => g.Id == game.Id);
            if (existing != null)
            {
                existing.IsDetected = true;
                existing.ProcessId = game.ProcessId;
                existing.GameDirectory = game.GameDirectory;
            }
        });
    }

    private void OnGameLost(object? sender, string gameId)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var existing = GameLibrary.Games.FirstOrDefault(g => g.Id == gameId);
            if (existing != null)
            {
                existing.IsDetected = false;
                existing.IsAttached = false;
                existing.ProcessId = 0;
                if (SelectedGame?.Id == gameId)
                {
                    AttachmentStatus = AttachmentStatus.Disconnected;
                    StatusText = "Game closed";
                    _espOverlay?.Close();
                    _espOverlay = null;
                }
            }
        });
    }

    [RelayCommand]
    private void SelectGame(GameProfile game)
    {
        SelectedGame = game;
        CheatPanel = new CheatPanelViewModel(game, _ipcService, _profileService);
        CheatPanel.LoadProfiles();
        IsSettingsOpen = false;
        
        if (game.IsAttached)
        {
            AttachmentStatus = AttachmentStatus.Connected;
            StatusText = "Connected";
        }
        else if (game.IsDetected)
        {
            AttachmentStatus = AttachmentStatus.Detecting;
            StatusText = "Game detected - Ready to attach";
        }
        else
        {
            AttachmentStatus = AttachmentStatus.Disconnected;
            StatusText = "Game not running";
        }
    }

    [RelayCommand]
    private async Task AttachToGameAsync()
    {
        if (SelectedGame == null) return;

        try
        {
            AttachmentStatus = AttachmentStatus.Injecting;
            StatusText = "Injecting...";

            var success = await _injectionService.InjectAsync(SelectedGame);

            if (success)
            {
                if (SelectedGame.Runtime == UnityRuntime.Mono)
                {
                    await _ipcService.ConnectAsync(SelectedGame.ProcessId);
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    _espOverlay = new Views.TransparentEspOverlay(SelectedGame.ProcessId, SelectedGame.ProcessName);
                    _espOverlay.Show();
                });

                SelectedGame.IsAttached = true;
                AttachmentStatus = AttachmentStatus.Connected;
                StatusText = "Connected";
            }
            else
            {
                AttachmentStatus = AttachmentStatus.Error;
                StatusText = "Injection failed";
            }
        }
        catch (Exception ex)
        {
            AttachmentStatus = AttachmentStatus.Error;
            StatusText = $"Error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DetachFromGameAsync()
    {
        if (SelectedGame == null) return;

        await _ipcService.DisconnectAsync();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _espOverlay?.Close();
            _espOverlay = null;
        });

        SelectedGame.IsAttached = false;
        AttachmentStatus = AttachmentStatus.Disconnected;
        StatusText = "Disconnected";
    }

    [RelayCommand]
    private void ToggleSettings()
    {
        IsSettingsOpen = !IsSettingsOpen;
    }

    [RelayCommand]
    private void OpenAddGameDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe",
            Title = "Select Unity Game Executable"
        };

        if (dialog.ShowDialog() == true)
        {
            var gamePath = dialog.FileName;
            var gameDir = System.IO.Path.GetDirectoryName(gamePath) ?? string.Empty;
            var gameName = System.IO.Path.GetFileNameWithoutExtension(gamePath);
            
            var runtime = OwnWand.Core.Helpers.RuntimeDetector.DetectFromDirectory(gameDir);
            var profile = new GameProfile
            {
                Id = $"custom_{gameName.ToLowerInvariant().Replace(" ", "_")}",
                Name = gameName,
                ProcessName = gameName,
                Runtime = runtime,
                CustomExePath = gamePath,
                GameDirectory = gameDir,
                IsCustomGame = true,
                Categories = new List<string> { "Custom" },
                Features = _presetService.GetGenericFeatures()
            };

            GameLibrary.AddGame(profile);
            _settingsService.AddCustomGame(profile);
            SelectGame(profile);
        }
    }

    partial void OnSearchQueryChanged(string value)
    {
        GameLibrary.FilterGames(value);
    }

    [RelayCommand]
    private void SetCustomExePath()
    {
        if (SelectedGame == null) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Executable Files (*.exe)|*.exe",
            Title = $"Select Executable for {SelectedGame.Name}"
        };

        if (dialog.ShowDialog() == true)
        {
            var gamePath = dialog.FileName;
            var gameDir = System.IO.Path.GetDirectoryName(gamePath) ?? string.Empty;
            var customProcessName = System.IO.Path.GetFileNameWithoutExtension(gamePath);

            _settingsService.SetCustomExePath(SelectedGame.Id, gamePath);
            
            SelectedGame.CustomExePath = gamePath;
            SelectedGame.GameDirectory = gameDir;
            if (!string.IsNullOrEmpty(customProcessName))
            {
                SelectedGame.ProcessName = customProcessName;
            }

            if (SelectedGame.Runtime == UnityRuntime.Unknown && !string.IsNullOrEmpty(gameDir))
            {
                SelectedGame.Runtime = OwnWand.Core.Helpers.RuntimeDetector.DetectFromDirectory(gameDir);
            }

            _gameDetectionService.UpdateCustomExePath(SelectedGame.Id, gamePath);

            _gameDetectionService.StopScanning();
            _gameDetectionService.StartScanning();

            if (SelectedGame.IsAttached)
            {
                AttachmentStatus = AttachmentStatus.Connected;
                StatusText = "Connected";
            }
            else if (SelectedGame.IsDetected)
            {
                AttachmentStatus = AttachmentStatus.Detecting;
                StatusText = "Game detected - Ready to attach";
            }
            else
            {
                AttachmentStatus = AttachmentStatus.Disconnected;
                StatusText = "Game not running";
            }
        }
    }

    [RelayCommand]
    private void ClearCustomExePath()
    {
        if (SelectedGame == null) return;

        _settingsService.RemoveCustomExePath(SelectedGame.Id);
        
        SelectedGame.CustomExePath = null;
        SelectedGame.GameDirectory = null;

        var presets = _presetService.LoadPresets();
        var original = presets.FirstOrDefault(p => p.Id == SelectedGame.Id);
        if (original != null)
        {
            SelectedGame.ProcessName = original.ProcessName;
            SelectedGame.Runtime = original.Runtime;
        }

        _gameDetectionService.RemoveCustomExePath(SelectedGame.Id);

        _gameDetectionService.StopScanning();
        _gameDetectionService.StartScanning();

        if (SelectedGame.IsAttached)
        {
            AttachmentStatus = AttachmentStatus.Connected;
            StatusText = "Connected";
        }
        else if (SelectedGame.IsDetected)
        {
            AttachmentStatus = AttachmentStatus.Detecting;
            StatusText = "Game detected - Ready to attach";
        }
        else
        {
            AttachmentStatus = AttachmentStatus.Disconnected;
            StatusText = "Game not running";
        }
    }
}
