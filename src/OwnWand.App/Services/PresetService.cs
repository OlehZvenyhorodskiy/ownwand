using System.IO;
using System.Text.Json;
using OwnWand.Core.Models;

namespace OwnWand.App.Services;

public class PresetService
{
    private readonly string _presetsDir;

    public PresetService()
    {
        _presetsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Presets");
    }

    public List<GameProfile> LoadPresets()
    {
        var profiles = new List<GameProfile>();

        try
        {
            if (!Directory.Exists(_presetsDir))
            {
                return profiles;
            }

            var files = Directory.GetFiles(_presetsDir, "*.json");
            foreach (var file in files)
            {
                // Skip categories.json if it is there
                if (Path.GetFileName(file).Equals("categories.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(file);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    var profile = JsonSerializer.Deserialize<GameProfile>(json, options);

                    if (profile != null)
                    {
                        profiles.Add(profile);
                    }
                }
                catch
                {
                    // Skip invalid presets
                }
            }
        }
        catch
        {
            // Ignored
        }

        return profiles;
    }

    public List<CheatFeature> GetGenericFeatures()
    {
        return new List<CheatFeature>
        {
            new()
            {
                Id = "god_mode",
                Category = "Player",
                Name = "God Mode",
                Description = "Toggle invincibility",
                Type = FeatureType.Toggle,
                DefaultEnabled = false,
                HookTarget = new HookTarget { ClassName = "PlayerHealth", MethodName = "TakeDamage", Action = "skip" }
            },
            new()
            {
                Id = "infinite_stamina",
                Category = "Player",
                Name = "Infinite Stamina",
                Description = "Never run out of stamina",
                Type = FeatureType.Toggle,
                DefaultEnabled = false,
                HookTarget = new HookTarget { ClassName = "PlayerMovement", MethodName = "Update", Action = "set_field", Field = "stamina", Value = "1" }
            },
            new()
            {
                Id = "speed_multiplier",
                Category = "Movement",
                Name = "Speed Multiplier",
                Description = "Modify walking speed",
                Type = FeatureType.Slider,
                Min = 1.0f,
                Max = 10.0f,
                DefaultValue = 1.0f,
                Step = 0.5f,
                HookTarget = new HookTarget { ClassName = "PlayerMovement", MethodName = "Update", Action = "multiply_field", Field = "speed", Value = "slider" }
            },
            new()
            {
                Id = "jump_height",
                Category = "Movement",
                Name = "Jump Height",
                Description = "Modify jump height multiplier",
                Type = FeatureType.Slider,
                Min = 1.0f,
                Max = 10.0f,
                DefaultValue = 1.0f,
                Step = 0.5f,
                HookTarget = new HookTarget { ClassName = "PlayerMovement", MethodName = "Jump", Action = "multiply_field", Field = "jumpForce", Value = "slider" }
            },
            new()
            {
                Id = "game_speed",
                Category = "World",
                Name = "Game Speed",
                Description = "Modify Unity timeScale",
                Type = FeatureType.Slider,
                Min = 0.1f,
                Max = 5.0f,
                DefaultValue = 1.0f,
                Step = 0.1f,
                HookTarget = new HookTarget { Namespace = "UnityEngine", ClassName = "Time", MethodName = "set_timeScale", Action = "override_return", Value = "slider" }
            }
        };
    }
}
