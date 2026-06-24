using System.Text.Json.Serialization;

namespace OwnWand.Core.Models;

/// <summary>
/// Message sent between the WPF app and the injected payload via Named Pipes.
/// </summary>
public class IpcMessage
{
    public string Type { get; set; } = string.Empty;
    public string FeatureId { get; set; } = string.Empty;
    public bool? Enabled { get; set; }
    public float? Value { get; set; }
    public string? Error { get; set; }
    public string? Data { get; set; }

    public static IpcMessage EnableFeature(string featureId) => new() { Type = "ENABLE", FeatureId = featureId, Enabled = true };
    public static IpcMessage DisableFeature(string featureId) => new() { Type = "DISABLE", FeatureId = featureId, Enabled = false };
    public static IpcMessage SetValue(string featureId, float value) => new() { Type = "SET_VALUE", FeatureId = featureId, Value = value };
    public static IpcMessage Status(string featureId, bool enabled, float value) => new() { Type = "STATUS", FeatureId = featureId, Enabled = enabled, Value = value };
    public static IpcMessage ErrorMessage(string error) => new() { Type = "ERROR", Error = error };
    public static IpcMessage Ping() => new() { Type = "PING" };
    public static IpcMessage Pong() => new() { Type = "PONG" };
}
