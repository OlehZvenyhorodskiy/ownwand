namespace OwnWand.Core.Models;

/// <summary>
/// User-saved profile for a specific game. Up to 5 profiles per game.
/// </summary>
public class UserProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Default";
    public string GameId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
    public Dictionary<string, FeatureState> FeatureStates { get; set; } = new();
}

/// <summary>
/// Saved state of a single feature within a profile.
/// </summary>
public class FeatureState
{
    public bool IsEnabled { get; set; }
    public float Value { get; set; }
}
