using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnWand.App.Services;
using OwnWand.Core.Models;
using OwnWand.Injector;

namespace OwnWand.App.ViewModels;

public partial class CheatPanelViewModel : ObservableObject, IDisposable
{
    private readonly IpcService _ipcService;
    private readonly ProfileService _profileService;
    
    // Cache to hold resolved patch addresses and original bytes: featureId -> (address, originalBytes)
    private static readonly Dictionary<string, (ulong Address, byte[] OriginalBytes)> NativePatchCache = new();

    [ObservableProperty]
    private GameProfile _game;

    [ObservableProperty]
    private ObservableCollection<UserProfile> _profiles = new();

    [ObservableProperty]
    private UserProfile? _selectedProfile;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    public ObservableCollection<CheatFeature> Features { get; }

    public CheatPanelViewModel(GameProfile game, IpcService ipcService, ProfileService profileService)
    {
        _game = game;
        _ipcService = ipcService;
        _profileService = profileService;
        Features = new ObservableCollection<CheatFeature>(game.Features);

        // Hook IPC service messages to receive status from game if needed
        _ipcService.MessageReceived += OnIpcMessageReceived;

        // Start Unreal Engine background stats freeze loop if applicable
        StartUnrealEngineFreezeLoop();
    }

    public void LoadProfiles()
    {
        var gameProfiles = _profileService.GetProfilesForGame(Game.Id);
        Profiles = new ObservableCollection<UserProfile>(gameProfiles);
        
        // Select default or first profile
        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void OnIpcMessageReceived(object? sender, IpcMessage msg)
    {
        if (msg.Type == "STATUS")
        {
            var feat = Features.FirstOrDefault(f => f.Id == msg.FeatureId);
            if (feat != null)
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    feat.IsEnabled = msg.Enabled ?? false;
                    feat.CurrentValue = msg.Value ?? feat.CurrentValue;
                });
            }
        }
    }

    [RelayCommand]
    public async Task ToggleFeatureAsync(CheatFeature feature)
    {
        if (feature.Id == "esp" || feature.Id.StartsWith("esp_"))
        {
            Views.TransparentEspOverlay.IsEspEnabled = Features.Any(f => (f.Id == "esp" || f.Id.StartsWith("esp_")) && f.IsEnabled);
        }
        
        if (Game.Runtime == UnityRuntime.Mono)
        {
            if (_ipcService.IsConnected)
            {
                var msg = feature.IsEnabled 
                    ? IpcMessage.EnableFeature(feature.Id) 
                    : IpcMessage.DisableFeature(feature.Id);
                await _ipcService.SendMessageAsync(msg);
            }
        }
        else
        {
            // Apply direct memory patch for native games
            await ApplyNativeMemoryPatchAsync(feature);
        }

        // Auto-save changes to the currently selected profile
        if (SelectedProfile != null)
        {
            SaveCurrentStateToProfile(SelectedProfile);
        }
    }

    [RelayCommand]
    public async Task SetFeatureValueAsync(CheatFeature feature)
    {
        if (Game.Runtime == UnityRuntime.Mono)
        {
            if (_ipcService.IsConnected)
            {
                var msg = IpcMessage.SetValue(feature.Id, feature.CurrentValue);
                await _ipcService.SendMessageAsync(msg);
            }
        }
        else
        {
            // Direct memory speed patch or multiplier value patch
            await ApplyNativeMemoryPatchAsync(feature);
        }

        // Auto-save changes to the currently selected profile
        if (SelectedProfile != null)
        {
            SaveCurrentStateToProfile(SelectedProfile);
        }
    }

    [RelayCommand]
    public void CreateProfile()
    {
        if (string.IsNullOrWhiteSpace(NewProfileName)) return;

        if (Profiles.Count >= 5)
        {
            System.Windows.MessageBox.Show(
                "You can have a maximum of 5 profiles per game.",
                "Limit Reached",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return;
        }

        var newProfile = new UserProfile
        {
            Name = NewProfileName.Trim(),
            GameId = Game.Id
        };

        SaveCurrentStateToProfile(newProfile);
        _profileService.SaveProfile(newProfile);
        
        Profiles.Add(newProfile);
        SelectedProfile = newProfile;
        NewProfileName = string.Empty;
    }

    [RelayCommand]
    public void DeleteProfile(UserProfile profile)
    {
        if (profile.Name == "Default")
        {
            System.Windows.MessageBox.Show(
                "Cannot delete the default profile.",
                "Action Blocked",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        _profileService.DeleteProfile(profile.Id);
        Profiles.Remove(profile);
        
        if (SelectedProfile?.Id == profile.Id)
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }
    }

    partial void OnSelectedProfileChanged(UserProfile? value)
    {
        if (value == null) return;

        // Apply profile settings to UI and send IPC
        foreach (var feature in Features)
        {
            if (value.FeatureStates.TryGetValue(feature.Id, out var state))
            {
                feature.IsEnabled = state.IsEnabled;
                feature.CurrentValue = state.Value;

                if (feature.Id == "esp" || feature.Id.StartsWith("esp_"))
                {
                    Views.TransparentEspOverlay.IsEspEnabled = Features.Any(f => (f.Id == "esp" || f.Id.StartsWith("esp_")) && f.IsEnabled);
                }

                if (Game.Runtime == UnityRuntime.Mono)
                {
                    if (_ipcService.IsConnected)
                    {
                        _ = SendFeatureStateAsync(feature);
                    }
                }
                else
                {
                    _ = ApplyNativeMemoryPatchAsync(feature);
                }
            }
        }
    }

    private async Task SendFeatureStateAsync(CheatFeature feature)
    {
        var toggleMsg = feature.IsEnabled 
            ? IpcMessage.EnableFeature(feature.Id) 
            : IpcMessage.DisableFeature(feature.Id);
        await _ipcService.SendMessageAsync(toggleMsg);

        if (feature.Type == FeatureType.Slider)
        {
            var valueMsg = IpcMessage.SetValue(feature.Id, feature.CurrentValue);
            await _ipcService.SendMessageAsync(valueMsg);
        }
    }

    private void SaveCurrentStateToProfile(UserProfile profile)
    {
        profile.FeatureStates.Clear();
        foreach (var feature in Features)
        {
            profile.FeatureStates[feature.Id] = new FeatureState
            {
                IsEnabled = feature.IsEnabled,
                Value = feature.CurrentValue
            };
        }
        _profileService.SaveProfile(profile);
    }

    private async Task ApplyNativeMemoryPatchAsync(CheatFeature feature)
    {
        if (feature.HookTarget == null)
        {
            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Feature '{feature.Name}' ({feature.Id}) has no HookTarget defined.");
            return;
        }

        if (string.IsNullOrEmpty(feature.HookTarget.Pattern) || string.IsNullOrEmpty(feature.HookTarget.PatchBytes))
        {
            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Warning: Feature '{feature.Name}' ({feature.Id}) has a Mono/Unity hook target (class: '{feature.HookTarget.ClassName}', method: '{feature.HookTarget.MethodName}'), but the game runtime is '{Game.Runtime}'. Native memory patching requires 'Pattern' and 'PatchBytes' to be defined. Skipping feature.");
            return;
        }

        OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Applying native patch for '{feature.Name}' ({feature.Id}). Enabled: {feature.IsEnabled}");

        await Task.Run(() =>
        {
            try
            {
                var processId = Game.ProcessId;
                if (processId <= 0)
                {
                    OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Error: Invalid ProcessId: {processId} for game '{Game.Name}'");
                    return;
                }

                string targetExeName = !string.IsNullOrEmpty(Game.CustomExePath) 
                    ? Path.GetFileName(Game.CustomExePath) 
                    : (Game.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Game.ProcessName : Game.ProcessName + ".exe");

                string moduleName = Game.Runtime == UnityRuntime.IL2CPP ? "GameAssembly.dll" : targetExeName;

                if (feature.IsEnabled)
                {
                    ulong address = 0;
                    byte[] originalBytes = Array.Empty<byte>();

                    if (NativePatchCache.TryGetValue(feature.Id, out var cached))
                    {
                        address = cached.Address;
                        originalBytes = cached.OriginalBytes;
                        OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Found cached address 0x{address:X} for '{feature.Name}'");
                    }
                    else
                    {
                        OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Scanning for pattern '{feature.HookTarget.Pattern}' in module '{moduleName}'...");
                        address = MemoryScanner.ScanPattern(processId, moduleName, feature.HookTarget.Pattern, out string scanError);
                        if (address == 0)
                        {
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Scan failed in module '{moduleName}'. Error: {scanError}. Trying fallback scan in '{targetExeName}'...");
                            address = MemoryScanner.ScanPattern(processId, targetExeName, feature.HookTarget.Pattern, out scanError);
                        }

                        if (address != 0)
                        {
                            if (feature.HookTarget.Offset.HasValue)
                            {
                                ulong originalAddress = address;
                                address = (ulong)((long)address + feature.HookTarget.Offset.Value);
                                OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Pattern found at 0x{originalAddress:X}. Applied offset {feature.HookTarget.Offset.Value} -> target address: 0x{address:X}");
                            }
                            else
                            {
                                OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Pattern found at target address: 0x{address:X}");
                            }
                        }
                        else
                        {
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Error: Pattern '{feature.HookTarget.Pattern}' was not found in process {processId}. Error detail: {scanError}");
                        }
                    }

                    if (address != 0)
                    {
                        var patchBytes = ParseHexBytes(feature.HookTarget.PatchBytes);
                        if (originalBytes.Length == 0)
                        {
                            originalBytes = ReadTargetMemory(processId, address, (uint)patchBytes.Length);
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Read original bytes at 0x{address:X}: {BitConverter.ToString(originalBytes)}");
                        }

                        OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Patching 0x{address:X} with bytes: {BitConverter.ToString(patchBytes)}");
                        bool success = MemoryScanner.PatchMemory(processId, address, patchBytes, out string patchError);
                        if (success)
                        {
                            NativePatchCache[feature.Id] = (address, originalBytes);
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Patch applied successfully for '{feature.Name}'");
                        }
                        else
                        {
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Error: Failed to write patch memory. Detail: {patchError}");
                        }
                    }
                }
                else
                {
                    if (NativePatchCache.TryGetValue(feature.Id, out var cached) && cached.Address != 0 && cached.OriginalBytes.Length > 0)
                    {
                        OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Restoring original bytes at 0x{cached.Address:X} for '{feature.Name}'");
                        bool success = MemoryScanner.PatchMemory(processId, cached.Address, cached.OriginalBytes, out string patchError);
                        if (success)
                        {
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Restored original bytes successfully.");
                        }
                        else
                        {
                            OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Error: Failed to restore original bytes. Detail: {patchError}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                OwnWand.Core.Helpers.Logger.Log($"[NativePatch] Exception inside ApplyNativeMemoryPatchAsync: {ex.Message}\n{ex.StackTrace}");
            }
        });
    }

    private static byte[] ParseHexBytes(string hex)
    {
        var parts = hex.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            bytes[i] = Convert.ToByte(parts[i], 16);
        }
        return bytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, uint nSize, out IntPtr lpNumberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static byte[] ReadTargetMemory(int processId, ulong address, uint size)
    {
        var buffer = new byte[size];
        IntPtr hProcess = OpenProcess(0x0010 /* PROCESS_VM_READ */, false, processId);
        if (hProcess != IntPtr.Zero)
        {
            try
            {
                ReadProcessMemory(hProcess, (IntPtr)address, buffer, size, out _);
            }
            finally
            {
                CloseHandle(hProcess);
            }
        }
        return buffer;
    }

    private bool _isDisposed;

    public void Dispose()
    {
        _isDisposed = true;
        _ipcService.MessageReceived -= OnIpcMessageReceived;
    }

    private void StartUnrealEngineFreezeLoop()
    {
        if (Game.EngineType != "UnrealEngine") return;

        Task.Run(async () =>
        {
            OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Starting background stats freeze loop for {Game.Name}...");
            
            ulong cachedGWorldAddress = 0;
            bool isUE5 = false;
            
            while (!_isDisposed)
            {
                try
                {
                    if (Game.IsAttached && Game.ProcessId > 0)
                    {
                        var freezeTargets = Features.Where(f => f.IsEnabled && f.HookTarget != null && 
                            (f.HookTarget.ExecutionMethod == "ValueFreeze" || f.HookTarget.ExecutionMethod == "ValueWrite")).ToList();

                        if (freezeTargets.Count > 0)
                        {
                            string targetExeName = !string.IsNullOrEmpty(Game.CustomExePath) 
                                ? Path.GetFileName(Game.CustomExePath) 
                                : (Game.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Game.ProcessName : Game.ProcessName + ".exe");

                            if (cachedGWorldAddress == 0)
                            {
                                string moduleName = targetExeName;
                                var detectResult = MemoryScanner.GetGWorldAddressAutoDetect(Game.ProcessId, moduleName);
                                cachedGWorldAddress = detectResult.GWorldPtr;
                                isUE5 = detectResult.IsUE5;
                                if (cachedGWorldAddress != 0)
                                {
                                    OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Resolved GWorld RIP address at 0x{cachedGWorldAddress:X} (UE5: {isUE5})");
                                }
                            }

                            if (cachedGWorldAddress != 0)
                            {
                                ulong uWorld = MemoryScanner.ReadPointer(Game.ProcessId, cachedGWorldAddress);
                                if (uWorld != 0)
                                {
                                    foreach (var target in freezeTargets)
                                    {
                                        if (target.HookTarget?.PointerChain != null && target.HookTarget.TargetOffset.HasValue)
                                        {
                                            int[] chain = (int[])target.HookTarget.PointerChain.Clone();
                                            int targetOffset = target.HookTarget.TargetOffset.GetValueOrDefault();

                                            if (isUE5)
                                            {
                                                for (int i = 0; i < chain.Length; i++)
                                                {
                                                    if (chain[i] == 384) chain[i] = 440; // GameInstance: 0x180 -> 0x1B8
                                                    else if (chain[i] == 672) chain[i] = 816; // AcknowledgedPawn: 0x2A0 -> 0x330
                                                    else if (chain[i] == 648) chain[i] = 664; // CharacterMovement: 0x288 -> 0x298
                                                }

                                                if (target.Id == "god_mode" && targetOffset == 416) targetOffset = 432; // Health: 0x1A0 -> 0x1B0
                                                else if (target.Id == "speed_multiplier" && targetOffset == 396) targetOffset = 448; // MaxWalkSpeed: 0x18C -> 0x1C0
                                                else if (target.Id == "jump_height" && targetOffset == 428) targetOffset = 480; // JumpZVelocity: 0x1AC -> 0x1E0
                                                else if (target.Id == "fly_mode" && targetOffset == 264) targetOffset = 276; // MovementMode: 0x108 -> 0x114
                                                else if (target.Id == "no_clip" && targetOffset == 92) targetOffset = 100; // Collision: 0x5C -> 0x64
                                            }

                                            ulong objAddress = MemoryScanner.ResolvePointerChain(Game.ProcessId, uWorld, chain);
                                            if (objAddress != 0)
                                            {
                                                if (Game.Id == "escape_the_backrooms" && target.Id == "speed_multiplier")
                                                {
                                                    float mult = target.CurrentValue;
                                                    float walk = 300.0f * mult;
                                                    float sprint = 600.0f * mult;
                                                    
                                                    MemoryScanner.WriteFloat(Game.ProcessId, objAddress + 0x988, walk, out _);
                                                    MemoryScanner.WriteFloat(Game.ProcessId, objAddress + 0x98C, sprint, out _);
                                                    MemoryScanner.WriteFloat(Game.ProcessId, objAddress + 0xE4C, walk, out _);
                                                    MemoryScanner.WriteFloat(Game.ProcessId, objAddress + 0xE58, sprint, out _);
                                                    MemoryScanner.WriteFloat(Game.ProcessId, objAddress + 0xE5C, sprint, out _);
                                                    
                                                    ulong charMov = MemoryScanner.ReadPointer(Game.ProcessId, objAddress + 0x288);
                                                    if (charMov != 0)
                                                    {
                                                        float currentMaxWalkSpeed = MemoryScanner.ReadFloat(Game.ProcessId, charMov + 0x18C);
                                                        float targetSpeed = 0f;
                                                        if (Math.Abs(currentMaxWalkSpeed - 300.0f) < 5.0f || Math.Abs(currentMaxWalkSpeed - walk) < 5.0f)
                                                        {
                                                            targetSpeed = walk;
                                                        }
                                                        else if (Math.Abs(currentMaxWalkSpeed - 600.0f) < 5.0f || Math.Abs(currentMaxWalkSpeed - sprint) < 5.0f)
                                                        {
                                                            targetSpeed = sprint;
                                                        }
                                                        else if (Math.Abs(currentMaxWalkSpeed - 100.0f) < 5.0f || Math.Abs(currentMaxWalkSpeed - (100.0f * mult)) < 5.0f)
                                                        {
                                                            targetSpeed = 100.0f * mult;
                                                        }

                                                        if (targetSpeed > 0)
                                                        {
                                                            MemoryScanner.WriteFloat(Game.ProcessId, charMov + 0x18C, targetSpeed, out _);
                                                        }
                                                    }
                                                    continue;
                                                }

                                                ulong targetAddress = objAddress + (ulong)targetOffset;
                                                float val = target.Type == FeatureType.Slider ? target.CurrentValue : (target.HookTarget.FreezeValue ?? 1.0f);
                                                
                                                if (target.Id == "speed_multiplier" && target.Type == FeatureType.Slider)
                                                {
                                                    val *= 600.0f; // Scale by base walk speed (600.0f)
                                                }
                                                else if (target.Id == "jump_height" && target.Type == FeatureType.Slider)
                                                {
                                                    val *= 1000.0f; // Scale by base jump velocity (1000.0f)
                                                }

                                                string error = string.Empty;
                                                bool success = false;
                                                
                                                if (target.HookTarget.DataType == "byte")
                                                {
                                                    success = MemoryScanner.WriteByte(Game.ProcessId, targetAddress, (byte)val, out error);
                                                }
                                                else if (target.HookTarget.DataType == "int")
                                                {
                                                    success = MemoryScanner.WriteInt32(Game.ProcessId, targetAddress, (int)val, out error);
                                                }
                                                else
                                                {
                                                    success = MemoryScanner.WriteFloat(Game.ProcessId, targetAddress, val, out error);
                                                }
                                                
                                                if (!success && !string.IsNullOrEmpty(error))
                                                {
                                                    OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Failed to write memory at 0x{targetAddress:X}: {error}");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        cachedGWorldAddress = 0;
                    }
                }
                catch (Exception ex)
                {
                    OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Exception: {ex.Message}");
                }

                await Task.Delay(100);
            }

            OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Stopped background stats freeze loop for {Game.Name}.");
        });
    }
}
