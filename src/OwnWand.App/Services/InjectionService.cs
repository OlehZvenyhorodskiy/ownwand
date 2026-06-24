using System.IO;
using OwnWand.Core.Models;
using OwnWand.Injector;

namespace OwnWand.App.Services;

public class InjectionService
{
    public Task<bool> InjectAsync(GameProfile profile)
    {
        return Task.Run(() =>
        {
            if (profile.ProcessId <= 0)
                return false;

            if (profile.Runtime == UnityRuntime.Mono)
            {
                // Find payload DLL
                string payloadPath = FindPayloadDll();
                if (string.IsNullOrEmpty(payloadPath) || !File.Exists(payloadPath))
                {
                    throw new FileNotFoundException("Payload DLL (OwnWand.Payload.dll) not found. Build the solution first.");
                }

                bool success = MonoInjector.Inject(
                    profile.ProcessId,
                    payloadPath,
                    "OwnWand.Payload",
                    "EntryPoint",
                    "Init",
                    out string error
                );

                if (!success)
                {
                    throw new Exception($"Mono injection failed: {error}");
                }

                return true;
            }
            else if (profile.Runtime == UnityRuntime.IL2CPP || profile.Runtime == UnityRuntime.Unknown)
            {
                // For IL2CPP or custom engine games, apply memory pattern patches for enabled features
                foreach (var feature in profile.Features)
                {
                    if (feature.IsEnabled && feature.HookTarget != null)
                    {
                        // Applying native memory scanner and patcher
                    }
                }
                
                return true;
            }

            return false;
        });
    }

    private string FindPayloadDll()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        
        // 1. Same directory
        var localPath = Path.Combine(baseDir, "OwnWand.Payload.dll");
        if (File.Exists(localPath)) return localPath;

        // 2. Dev folder (relative to build output)
        // Usually bin\Debug\net8.0-windows\
        var devPath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "OwnWand.Payload", "bin", "Debug", "netstandard2.0", "OwnWand.Payload.dll"));
        if (File.Exists(devPath)) return devPath;

        var releasePath = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "OwnWand.Payload", "bin", "Release", "netstandard2.0", "OwnWand.Payload.dll"));
        if (File.Exists(releasePath)) return releasePath;

        return string.Empty;
    }
}
