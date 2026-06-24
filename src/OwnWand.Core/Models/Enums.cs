namespace OwnWand.Core.Models;

public enum UnityRuntime
{
    Unknown,
    Mono,
    IL2CPP
}

public enum FeatureType
{
    Toggle,
    Slider,
    Input
}

public enum FeatureCategory
{
    Player,
    Movement,
    Combat,
    World,
    Resources
}

public enum AttachmentStatus
{
    Disconnected,
    Detecting,
    Injecting,
    Connected,
    Error
}

public enum IpcCommand
{
    EnableFeature,
    DisableFeature,
    SetValue,
    GetStatus,
    ListFeatures,
    Detach
}
