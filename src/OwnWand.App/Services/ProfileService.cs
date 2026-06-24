using System.IO;
using System.Text.Json;
using OwnWand.Core.Models;

namespace OwnWand.App.Services;

public class ProfileService
{
    private readonly string _profilesDir;
    private readonly Dictionary<string, List<UserProfile>> _cache = new();

    public ProfileService()
    {
        var appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OwnWand");
        _profilesDir = Path.Combine(appDataFolder, "profiles");
        Directory.CreateDirectory(_profilesDir);
        LoadAllProfiles();
    }

    private void LoadAllProfiles()
    {
        try
        {
            var files = Directory.GetFiles(_profilesDir, "*.json");
            foreach (var file in files)
            {
                var json = File.ReadAllText(file);
                var profile = JsonSerializer.Deserialize<UserProfile>(json);
                if (profile != null)
                {
                    if (!_cache.TryGetValue(profile.GameId, out var list))
                    {
                        list = new List<UserProfile>();
                        _cache[profile.GameId] = list;
                    }
                    list.Add(profile);
                }
            }
        }
        catch
        {
            // Ignored
        }
    }

    public List<UserProfile> GetProfilesForGame(string gameId)
    {
        if (_cache.TryGetValue(gameId, out var list))
        {
            return list.OrderBy(p => p.CreatedAt).ToList();
        }

        // If no profiles exist, create a default one
        var defaultProfile = CreateDefaultProfile(gameId);
        return new List<UserProfile> { defaultProfile };
    }

    public UserProfile CreateDefaultProfile(string gameId)
    {
        var profile = new UserProfile
        {
            Name = "Default",
            GameId = gameId
        };
        SaveProfile(profile);
        return profile;
    }

    public bool SaveProfile(UserProfile profile)
    {
        try
        {
            profile.ModifiedAt = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(profile, new JsonSerializerOptions { WriteIndented = true });
            var filePath = Path.Combine(_profilesDir, $"{profile.Id}.json");
            File.WriteAllText(filePath, json);

            // Update cache
            if (!_cache.TryGetValue(profile.GameId, out var list))
            {
                list = new List<UserProfile>();
                _cache[profile.GameId] = list;
            }

            var existing = list.FirstOrDefault(p => p.Id == profile.Id);
            if (existing != null)
            {
                list.Remove(existing);
            }
            list.Add(profile);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool DeleteProfile(string profileId)
    {
        try
        {
            var filePath = Path.Combine(_profilesDir, $"{profileId}.json");
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // Remove from cache
            foreach (var pair in _cache)
            {
                var existing = pair.Value.FirstOrDefault(p => p.Id == profileId);
                if (existing != null)
                {
                    pair.Value.Remove(existing);
                    return true;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
