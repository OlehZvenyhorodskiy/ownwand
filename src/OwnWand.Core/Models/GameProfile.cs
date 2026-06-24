using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OwnWand.Core.Models;

/// <summary>
/// Represents a game profile loaded from a JSON preset file.
/// </summary>
public class GameProfile : INotifyPropertyChanged
{
    private string _id = string.Empty;
    private string _name = string.Empty;
    private string _processName = string.Empty;
    private int _steamAppId;
    private UnityRuntime _runtime = UnityRuntime.Mono;
    private string _assemblyName = "Assembly-CSharp";
    private string _dataFolderPattern = "*_Data";
    private string _icon = string.Empty;
    private List<string> _categories = new();
    private List<CheatFeature> _features = new();
    private bool _isDetected;
    private bool _isAttached;
    private int _processId;
    private string? _gameDirectory;
    private string? _customExePath;
    private bool _isCustomGame;

    public string Id { get => _id; set => SetField(ref _id, value); }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string ProcessName { get => _processName; set => SetField(ref _processName, value); }
    public int SteamAppId { get => _steamAppId; set => SetField(ref _steamAppId, value); }
    public UnityRuntime Runtime { get => _runtime; set => SetField(ref _runtime, value); }
    public string AssemblyName { get => _assemblyName; set => SetField(ref _assemblyName, value); }
    public string DataFolderPattern { get => _dataFolderPattern; set => SetField(ref _dataFolderPattern, value); }
    public string Icon { get => _icon; set => SetField(ref _icon, value); }
    public List<string> Categories { get => _categories; set => SetField(ref _categories, value); }
    public List<CheatFeature> Features { get => _features; set => SetField(ref _features, value); }

    // Runtime state (not serialized)
    public bool IsDetected { get => _isDetected; set => SetField(ref _isDetected, value); }
    public bool IsAttached { get => _isAttached; set => SetField(ref _isAttached, value); }
    public int ProcessId { get => _processId; set => SetField(ref _processId, value); }
    public string? GameDirectory { get => _gameDirectory; set => SetField(ref _gameDirectory, value); }
    public string? CustomExePath 
    { 
        get => _customExePath; 
        set 
        {
            if (SetField(ref _customExePath, value))
            {
                OnPropertyChanged(nameof(HasCustomExePath));
            }
        }
    }
    public bool HasCustomExePath => !string.IsNullOrEmpty(CustomExePath);
    public bool IsCustomGame { get => _isCustomGame; set => SetField(ref _isCustomGame, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
