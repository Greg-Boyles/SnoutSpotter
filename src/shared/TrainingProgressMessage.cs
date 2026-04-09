using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

/// <summary>
/// MQTT message published to snoutspotter/trainer/{thingName}/progress.
/// Consumed by the UpdateTrainingProgress Lambda via an IoT Rule.
/// </summary>
public class TrainingProgressMessage
{
    [JsonPropertyName("job_id")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("progress")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrainingProgress? Progress { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrainingResult? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Error { get; init; }

    [JsonPropertyName("failed_stage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailedStage { get; init; }

    [JsonPropertyName("checkpoint_s3_key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CheckpointS3Key { get; init; }
}
