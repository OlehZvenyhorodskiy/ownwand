using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OwnWand.App.Services;
using OwnWand.Core.Models;
using OwnWand.Injector;

namespace OwnWand.App.ViewModels;

public partial class CheatPanelViewModel : ObservableObject
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
        if (feature.HookTarget == null) return;
        if (string.IsNullOrEmpty(feature.HookTarget.Pattern) || string.IsNullOrEmpty(feature.HookTarget.PatchBytes)) return;

        await Task.Run(() =>
        {
            try
            {
                var processId = Game.ProcessId;
                if (processId <= 0) return;

                string moduleName = Game.Runtime == UnityRuntime.IL2CPP ? "GameAssembly.dll" : Game.ProcessName + ".exe";

                if (feature.IsEnabled)
                {
                    ulong address = 0;
                    byte[] originalBytes = Array.Empty<byte>();

                    if (NativePatchCache.TryGetValue(feature.Id, out var cached))
                    {
                        address = cached.Address;
                        originalBytes = cached.OriginalBytes;
                    }
                    else
                    {
                        address = MemoryScanner.ScanPattern(processId, moduleName, feature.HookTarget.Pattern, out string scanError);
                        if (address == 0)
                        {
                            address = MemoryScanner.ScanPattern(processId, Game.ProcessName + ".exe", feature.HookTarget.Pattern, out scanError);
                        }

                        if (address != 0 && feature.HookTarget.Offset.HasValue)
                        {
                            address = (ulong)((long)address + feature.HookTarget.Offset.Value);
                        }
                    }

                    if (address != 0)
                    {
                        var patchBytes = ParseHexBytes(feature.HookTarget.PatchBytes);
                        if (originalBytes.Length == 0)
                        {
                            originalBytes = ReadTargetMemory(processId, address, (uint)patchBytes.Length);
                        }

                        bool success = MemoryScanner.PatchMemory(processId, address, patchBytes, out _);
                        if (success)
                        {
                            NativePatchCache[feature.Id] = (address, originalBytes);
                        }
                    }
                }
                else
                {
                    if (NativePatchCache.TryGetValue(feature.Id, out var cached) && cached.Address != 0 && cached.OriginalBytes.Length > 0)
                    {
                        MemoryScanner.PatchMemory(processId, cached.Address, cached.OriginalBytes, out _);
                    }
                }
            }
            catch
            {
                // Ignored
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
}
