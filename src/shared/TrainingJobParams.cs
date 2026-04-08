using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

/// <summary>Training job configuration parameters — shared between API, Lambda, and training agent.</summary>
public class TrainingJobParams
{
    [JsonPropertyName("epochs")]
    public int Epochs { get; init; } = 100;

    [JsonPropertyName("batch_size")]
    public int BatchSize { get; init; } = 16;

    [JsonPropertyName("image_size")]
    public int ImageSize { get; init; } = 640;

    [JsonPropertyName("learning_rate")]
    public double LearningRate { get; init; } = 0.01;

    [JsonPropertyName("workers")]
    public int Workers { get; init; } = 8;

    [JsonPropertyName("model_base")]
    public string ModelBase { get; init; } = "yolov8n.pt";

    [JsonPropertyName("resume_from")]
    public string? ResumeFrom { get; init; }
}
