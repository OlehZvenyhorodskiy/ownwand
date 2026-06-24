namespace OwnWand.Core.Models;

/// <summary>
/// Application-wide settings persisted to disk.
/// </summary>
public class AppSettings
{
    public bool OverlayEnabled { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoDetectGames { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool CheckForUpdates { get; set; } = true;
    public int GameScanIntervalSeconds { get; set; } = 5;
    public List<CustomGameEntry> CustomGames { get; set; } = new();
    public string Theme { get; set; } = "Dark";
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
}

/// <summary>
/// A custom game added by the user.
/// </summary>
public class CustomGameEntry
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ExePath { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
}
