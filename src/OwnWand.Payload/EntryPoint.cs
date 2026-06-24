using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OwnWand.Payload;

public static class EntryPoint
{
    // The entry point invoked by the injector
    public static void Init()
    {
        try
        {
            // Start the IPC server
            IpcServer.Start();
            IpcServer.CommandReceived += OnCommandReceived;

            // Send a ping response once startup is confirmed
            SendStatusMessage("Payload initialized inside process.");
        }
        catch (Exception ex)
        {
            SendErrorMessage($"Initialization error: {ex.Message}");
        }
    }

    private static void OnCommandReceived(string json)
    {
        try
        {
            var msg = JsonSerializer.Deserialize<IpcMessageInternal>(json);
            if (msg == null) return;

            switch (msg.Type?.ToUpperInvariant())
            {
                case "ENABLE":
                    HookHandler.ToggleFeature(msg.FeatureId, true);
                    SendStatusUpdate(msg.FeatureId, true, msg.Value ?? 1f);
                    break;

                case "DISABLE":
                    HookHandler.ToggleFeature(msg.FeatureId, false);
                    SendStatusUpdate(msg.FeatureId, false, msg.Value ?? 1f);
                    break;

                case "SET_VALUE":
                    if (msg.Value.HasValue)
                    {
                        HookHandler.SetFeatureValue(msg.FeatureId, msg.Value.Value);
                        SendStatusUpdate(msg.FeatureId, true, msg.Value.Value);
                    }
                    break;

                case "PING":
                    IpcServer.SendMessage("{\"Type\":\"PONG\"}");
                    break;
            }
        }
        catch (Exception ex)
        {
            SendErrorMessage($"Error handling command: {ex.Message}");
        }
    }

    private static void SendStatusUpdate(string featureId, bool enabled, float value)
    {
        var response = new IpcMessageInternal
        {
            Type = "STATUS",
            FeatureId = featureId,
            Enabled = enabled,
            Value = value
        };
        var json = JsonSerializer.Serialize(response);
        IpcServer.SendMessage(json);
    }

    private static void SendStatusMessage(string info)
    {
        var response = new IpcMessageInternal
        {
            Type = "INFO",
            Data = info
        };
        var json = JsonSerializer.Serialize(response);
        IpcServer.SendMessage(json);
    }

    private static void SendErrorMessage(string error)
    {
        var response = new IpcMessageInternal
        {
            Type = "ERROR",
            Error = error
        };
        var json = JsonSerializer.Serialize(response);
        IpcServer.SendMessage(json);
    }
}

internal class IpcMessageInternal
{
    [JsonPropertyName("Type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("FeatureId")]
    public string FeatureId { get; set; } = string.Empty;

    [JsonPropertyName("Enabled")]
    public bool? Enabled { get; set; }

    [JsonPropertyName("Value")]
    public float? Value { get; set; }

    [JsonPropertyName("Error")]
    public string? Error { get; set; }

    [JsonPropertyName("Data")]
    public string? Data { get; set; }
}
