using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

public class TrainingProgress
{
    [JsonPropertyName("epoch")]
    public int Epoch { get; init; }

    [JsonPropertyName("total_epochs")]
    public int TotalEpochs { get; init; }

    [JsonPropertyName("train_loss")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TrainLoss { get; init; }

    [JsonPropertyName("val_loss")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? ValLoss { get; init; }

    [JsonPropertyName("mAP50")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MAP50 { get; init; }

    [JsonPropertyName("mAP50_95")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? MAP50_95 { get; init; }

    [JsonPropertyName("best_mAP50")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? BestMAP50 { get; init; }

    [JsonPropertyName("elapsed_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ElapsedSeconds { get; init; }

    [JsonPropertyName("eta_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EtaSeconds { get; init; }

    [JsonPropertyName("gpu_util_percent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GpuUtilPercent { get; init; }

    [JsonPropertyName("gpu_temp_c")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? GpuTempC { get; init; }

    [JsonPropertyName("download_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DownloadBytes { get; init; }

    [JsonPropertyName("download_total_bytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DownloadTotalBytes { get; init; }

    [JsonPropertyName("download_speed_mbps")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DownloadSpeedMbps { get; init; }
}
