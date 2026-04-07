using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

public class TrainingResult
{
    [JsonPropertyName("model_s3_key")]
    public string ModelS3Key { get; init; } = "";

    [JsonPropertyName("model_size_mb")]
    public double ModelSizeMb { get; init; }

    [JsonPropertyName("final_mAP50")]
    public double FinalMAP50 { get; init; }

    [JsonPropertyName("total_epochs")]
    public int TotalEpochs { get; init; }

    [JsonPropertyName("best_epoch")]
    public int BestEpoch { get; init; }

    [JsonPropertyName("training_time_seconds")]
    public long TrainingTimeSeconds { get; init; }

    [JsonPropertyName("dataset_images")]
    public int DatasetImages { get; init; }

    [JsonPropertyName("classes")]
    public string[] Classes { get; init; } = [];

    [JsonPropertyName("final_mAP50_95")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? FinalMAP50_95 { get; init; }

    [JsonPropertyName("precision")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Precision { get; init; }

    [JsonPropertyName("recall")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Recall { get; init; }
}
