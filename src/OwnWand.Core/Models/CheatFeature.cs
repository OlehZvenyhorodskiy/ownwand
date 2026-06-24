namespace OwnWand.Core.Models;

/// <summary>
/// Represents a single cheat feature (toggle, slider, or input).
/// </summary>
public class CheatFeature
{
    public string Id { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public FeatureType Type { get; set; } = FeatureType.Toggle;
    public bool DefaultEnabled { get; set; }
    public float Min { get; set; } = 0f;
    public float Max { get; set; } = 10f;
    public float DefaultValue { get; set; } = 1f;
    public float Step { get; set; } = 0.1f;
    public HookTarget? HookTarget { get; set; }

    // Runtime state
    public bool IsEnabled { get; set; }
    public float CurrentValue { get; set; }
}
