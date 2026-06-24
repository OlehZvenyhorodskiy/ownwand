namespace OwnWand.Core.Models;

/// <summary>
/// Represents a game profile loaded from a JSON preset file.
/// </summary>
public class GameProfile
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ProcessName { get; set; } = string.Empty;
    public int SteamAppId { get; set; }
    public UnityRuntime Runtime { get; set; } = UnityRuntime.Mono;
    public string AssemblyName { get; set; } = "Assembly-CSharp";
    public string DataFolderPattern { get; set; } = "*_Data";
    public string Icon { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = new();
    public List<CheatFeature> Features { get; set; } = new();

    // Runtime state (not serialized)
    public bool IsDetected { get; set; }
    public bool IsAttached { get; set; }
    public int ProcessId { get; set; }
    public string? GameDirectory { get; set; }
    public string? CustomExePath { get; set; } // User-defined path to .exe
    public bool IsCustomGame { get; set; } // Whether user added this manually
}
