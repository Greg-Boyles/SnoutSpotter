using System.Text.Json.Serialization;

namespace SnoutSpotter.Contracts;

/// <summary>
/// SQS message for the training job queue.
/// Produced by API (TrainingService), consumed by Training Agent.
/// Queue: snout-spotter-training-jobs-queue
/// </summary>
public record TrainingJobMessage(
    [property: JsonPropertyName("jobId")] string JobId,
    [property: JsonPropertyName("exportS3Key")] string ExportS3Key,
    [property: JsonPropertyName("config")] TrainingJobParamsMessage Config,
    [property: JsonPropertyName("jobType")] string JobType = "detector");

/// <summary>Training configuration included in the SQS message.</summary>
public record TrainingJobParamsMessage(
    [property: JsonPropertyName("epochs")] int Epochs = 100,
    [property: JsonPropertyName("batch_size")] int BatchSize = 16,
    [property: JsonPropertyName("image_size")] int ImageSize = 640,
    [property: JsonPropertyName("learning_rate")] double LearningRate = 0.01,
    [property: JsonPropertyName("workers")] int Workers = 8,
    [property: JsonPropertyName("model_base")] string ModelBase = "yolov8n.pt",
    [property: JsonPropertyName("resume_from")] string? ResumeFrom = null);
