using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

/// <summary>Shadow desired state for a training job dispatch.</summary>
public class TrainingJobDesired
{
    [JsonPropertyName("jobId")]
    public string JobId { get; init; } = "";

    [JsonPropertyName("exportS3Key")]
    public string ExportS3Key { get; init; } = "";

    [JsonPropertyName("config")]
    public TrainingJobParams Config { get; init; } = new();
}