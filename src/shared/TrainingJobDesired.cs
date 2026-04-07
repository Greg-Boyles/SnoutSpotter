using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

/// <summary>Shadow desired state for a training job dispatch.</summary>
public class TrainingJobDesired
{
    [JsonPropertyName("jobId")]       public string           JobId       { get; init; } = "";
    [JsonPropertyName("exportS3Key")] public string           ExportS3Key { get; init; } = "";
    [JsonPropertyName("config")]      public TrainingJobParams Config      { get; init; } = new();
}

/// <summary>Hyperparameter config nested inside TrainingJobDesired.</summary>
public class TrainingJobParams
{
    [JsonPropertyName("epochs")]         public int     Epochs       { get; init; } = 100;
    [JsonPropertyName("batch_size")]     public int     BatchSize    { get; init; } = 16;
    [JsonPropertyName("image_size")]     public int     ImageSize    { get; init; } = 640;
    [JsonPropertyName("learning_rate")]  public double  LearningRate { get; init; } = 0.01;
    [JsonPropertyName("workers")]        public int     Workers      { get; init; } = 8;
    [JsonPropertyName("model_base")]     public string  ModelBase    { get; init; } = "yolov8n.pt";

    [JsonPropertyName("resume_from")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResumeFrom { get; init; }
}
