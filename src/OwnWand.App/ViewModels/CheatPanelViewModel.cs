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
        feature.IsEnabled = !feature.IsEnabled;

        if (feature.Id == "esp")
        {
            Views.TransparentEspOverlay.IsEspEnabled = feature.IsEnabled;
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

                if (feature.Id == "esp")
                {
                    Views.TransparentEspOverlay.IsEspEnabled = feature.IsEnabled;
                }

                if (_ipcService.IsConnected)
                {
                    _ = SendFeatureStateAsync(feature);
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
        if (Game.Id != "escape_the_backrooms") return;

        Task.Run(async () =>
        {
            OwnWand.Core.Helpers.Logger.Log("[UnrealLoop] Starting background stats freeze loop for Escape the Backrooms...");
            
            ulong cachedGWorldAddress = 0;
            
            while (!_isDisposed && Game.IsAttached && Game.ProcessId > 0)
            {
                try
                {
                    bool needStamina = Features.Any(f => f.Id == "infinite_stamina" && f.IsEnabled);
                    bool needSanity = Features.Any(f => f.Id == "infinite_sanity" && f.IsEnabled);

                    if (needStamina || needSanity)
                    {
                        string targetExeName = !string.IsNullOrEmpty(Game.CustomExePath) 
                            ? Path.GetFileName(Game.CustomExePath) 
                            : "Backrooms-Win64-Shipping.exe";

                        if (cachedGWorldAddress == 0)
                        {
                            cachedGWorldAddress = MemoryScanner.GetGWorldAddress(Game.ProcessId, targetExeName);
                            if (cachedGWorldAddress != 0)
                            {
                                OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Resolved GWorld RIP address at 0x{cachedGWorldAddress:X}");
                            }
                        }

                        if (cachedGWorldAddress != 0)
                        {
                            ulong uWorld = MemoryScanner.ReadPointer(Game.ProcessId, cachedGWorldAddress);
                            if (uWorld != 0)
                            {
                                ulong gameInstance = MemoryScanner.ReadPointer(Game.ProcessId, uWorld + 0x1A8);
                                if (gameInstance != 0)
                                {
                                    ulong localPlayersArray = MemoryScanner.ReadPointer(Game.ProcessId, gameInstance + 0x38);
                                    if (localPlayersArray != 0)
                                    {
                                        ulong localPlayer = MemoryScanner.ReadPointer(Game.ProcessId, localPlayersArray);
                                        if (localPlayer != 0)
                                        {
                                            ulong playerController = MemoryScanner.ReadPointer(Game.ProcessId, localPlayer + 0x30);
                                            if (playerController != 0)
                                            {
                                                ulong acknowledgedPawn = MemoryScanner.ReadPointer(Game.ProcessId, playerController + 0x2A0);
                                                if (acknowledgedPawn != 0)
                                                {
                                                    if (needStamina)
                                                    {
                                                        byte[] staminaBytes = BitConverter.GetBytes(100.0f);
                                                        MemoryScanner.PatchMemory(Game.ProcessId, acknowledgedPawn + 0x5F0, staminaBytes, out _);
                                                    }
                                                    if (needSanity)
                                                    {
                                                        byte[] sanityBytes = BitConverter.GetBytes(100.0f);
                                                        MemoryScanner.PatchMemory(Game.ProcessId, acknowledgedPawn + 0x604, sanityBytes, out _);
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    OwnWand.Core.Helpers.Logger.Log($"[UnrealLoop] Exception: {ex.Message}");
                }

                await Task.Delay(100);
            }

            OwnWand.Core.Helpers.Logger.Log("[UnrealLoop] Stopped background stats freeze loop.");
        });
    }
}
