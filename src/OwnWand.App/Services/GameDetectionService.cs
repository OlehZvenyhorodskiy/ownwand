using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OwnWand.Core.Helpers;
using OwnWand.Core.Models;

namespace OwnWand.App.Services;

public class GameDetectionService
{
    private readonly PresetService _presetService;
    private readonly ProcessService _processService;
    private readonly SettingsService _settingsService;
    private readonly Dictionary<string, int> _detectedGames = new(); // gameId -> processId
    private readonly List<GameProfile> _gamesToScan = new();
    private CancellationTokenSource? _scanCts;
    private bool _isScanning;

    public event EventHandler<GameProfile>? GameDetected;
    public event EventHandler<string>? GameLost; // gameId

    public GameDetectionService(
        PresetService presetService,
        ProcessService processService,
        SettingsService settingsService)
    {
        _presetService = presetService;
        _processService = processService;
        _settingsService = settingsService;
    }

    public void StartScanning()
    {
        if (_isScanning) return;
        _isScanning = true;

        // Load scanning targets
        _gamesToScan.Clear();
        var presets = _presetService.LoadPresets();
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
        _gamesToScan.AddRange(presets);
        
        foreach (var customGame in _settingsService.Settings.CustomGames)
        {
            var gameDir = Path.GetDirectoryName(customGame.ExePath) ?? string.Empty;
            var runtime = RuntimeDetector.DetectFromDirectory(gameDir);
            _gamesToScan.Add(new GameProfile
            {
                Id = customGame.Id,
                Name = customGame.Name,
                ProcessName = customGame.ProcessName,
                CustomExePath = customGame.ExePath,
                GameDirectory = gameDir,
                Runtime = runtime,
                IsCustomGame = true,
                Features = _presetService.GetGenericFeatures()
            });
        }

        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    ScanForGames();
                }
                catch
                {
                    // Ignore errors in scan loop
                }

                var interval = _settingsService.Settings.GameScanIntervalSeconds;
                if (interval < 1) interval = 5;
                await Task.Delay(TimeSpan.FromSeconds(interval), token);
            }
        }, token);
    }

    public void StopScanning()
    {
        _scanCts?.Cancel();
        _isScanning = false;
    }

    public void RegisterCustomGame(GameProfile customGame)
    {
        lock (_gamesToScan)
        {
            if (!_gamesToScan.Any(g => g.Id == customGame.Id))
            {
                _gamesToScan.Add(customGame);
            }
        }
    }

    public void UpdateCustomExePath(string gameId, string path)
    {
        lock (_gamesToScan)
        {
            var game = _gamesToScan.FirstOrDefault(g => g.Id == gameId);
            if (game != null)
            {
                game.CustomExePath = path;
                game.GameDirectory = Path.GetDirectoryName(path);
                var customProcessName = Path.GetFileNameWithoutExtension(path);
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
    }

    public void RemoveCustomExePath(string gameId)
    {
        lock (_gamesToScan)
        {
            var game = _gamesToScan.FirstOrDefault(g => g.Id == gameId);
            if (game != null)
            {
                game.CustomExePath = null;
                game.GameDirectory = null;
                var presets = _presetService.LoadPresets();
                var original = presets.FirstOrDefault(p => p.Id == gameId);
                if (original != null)
                {
                    game.ProcessName = original.ProcessName;
                    game.Runtime = original.Runtime;
                }
            }
        }
    }

    private void ScanForGames()
    {
        List<GameProfile> targets;
        lock (_gamesToScan)
        {
            targets = new List<GameProfile>(_gamesToScan);
        }

        // 1. Check if currently detected games are still running
        var lostGames = new List<string>();
        foreach (var detected in _detectedGames)
        {
            if (!_processService.IsProcessRunning(detected.Value))
            {
                lostGames.Add(detected.Key);
            }
        }

        foreach (var lostId in lostGames)
        {
            _detectedGames.Remove(lostId);
            GameLost?.Invoke(this, lostId);
        }

        // 2. Scan for running processes that match our games list
        foreach (var game in targets)
        {
            if (_detectedGames.ContainsKey(game.Id))
                continue; // Already running and detected

            var process = _processService.FindProcessByName(game.ProcessName);
            if (process != null && !process.HasExited)
            {
                // Verify/update runtime detection
                var path = _processService.GetProcessExecutablePath(process);
                if (!string.IsNullOrEmpty(path))
                {
                    game.GameDirectory = Path.GetDirectoryName(path);
                    if (game.Runtime == UnityRuntime.Unknown)
                    {
                        game.Runtime = RuntimeDetector.DetectFromProcess(process);
                        if (game.Runtime == UnityRuntime.Unknown && !string.IsNullOrEmpty(game.GameDirectory))
                        {
                            game.Runtime = RuntimeDetector.DetectFromDirectory(game.GameDirectory);
                        }
                    }
                }

                game.ProcessId = process.Id;
                game.IsDetected = true;
                _detectedGames[game.Id] = process.Id;
                GameDetected?.Invoke(this, game);
            }
        }
    }
}
