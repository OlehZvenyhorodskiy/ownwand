using System.Diagnostics;
using OwnWand.Core.Models;

namespace OwnWand.Core.Helpers;

/// <summary>
/// Detects whether a Unity game uses Mono or IL2CPP runtime.
/// </summary>
public static class RuntimeDetector
{
    /// <summary>
    /// Detect Unity backend from the game's directory structure.
    /// </summary>
    public static UnityRuntime DetectFromDirectory(string gameDirectory)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
            return UnityRuntime.Unknown;

        // Check for IL2CPP indicators
        if (File.Exists(Path.Combine(gameDirectory, "GameAssembly.dll")))
            return UnityRuntime.IL2CPP;

        // Check for Mono indicators
        if (Directory.Exists(Path.Combine(gameDirectory, "MonoBleedingEdge")))
            return UnityRuntime.Mono;

        // Check in Data folder
        var dataFolders = Directory.GetDirectories(gameDirectory, "*_Data");
        foreach (var dataFolder in dataFolders)
        {
            if (Directory.Exists(Path.Combine(dataFolder, "il2cpp_data")))
                return UnityRuntime.IL2CPP;

            var managedDir = Path.Combine(dataFolder, "Managed");
            if (Directory.Exists(managedDir) && File.Exists(Path.Combine(managedDir, "Assembly-CSharp.dll")))
                return UnityRuntime.Mono;
        }

        // Check for mono DLLs in root
        if (File.Exists(Path.Combine(gameDirectory, "mono.dll")) ||
            File.Exists(Path.Combine(gameDirectory, "mono-2.0-bdwgc.dll")))
            return UnityRuntime.Mono;

        return UnityRuntime.Unknown;
    }

    /// <summary>
    /// Detect Unity backend from a running process's loaded modules.
    /// </summary>
    public static UnityRuntime DetectFromProcess(Process process)
    {
        try
        {
            foreach (ProcessModule module in process.Modules)
            {
                var name = module.ModuleName ?? string.Empty;

                if (name.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    return UnityRuntime.IL2CPP;

                if ((name.StartsWith("mono", StringComparison.OrdinalIgnoreCase) ||
                     name.Contains("mono", StringComparison.OrdinalIgnoreCase)) &&
                    name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return UnityRuntime.Mono;
            }
        }
        catch (Exception)
        {
            // Access denied or process exited
        }

        return UnityRuntime.Unknown;
    }

    /// <summary>
    /// Check if a directory contains a Unity game.
    /// </summary>
    public static bool IsUnityGame(string gameDirectory)
    {
        if (string.IsNullOrEmpty(gameDirectory) || !Directory.Exists(gameDirectory))
            return false;

        // Check for Unity data folder
        var dataFolders = Directory.GetDirectories(gameDirectory, "*_Data");
        if (dataFolders.Length == 0)
            return false;

        // Check for UnityPlayer.dll or unity default runtime files
        if (File.Exists(Path.Combine(gameDirectory, "UnityPlayer.dll")))
            return true;

        // Check for managed or il2cpp data
        foreach (var dataFolder in dataFolders)
        {
            if (Directory.Exists(Path.Combine(dataFolder, "Managed")) ||
                Directory.Exists(Path.Combine(dataFolder, "il2cpp_data")))
                return true;
        }

        return false;
    }
}
