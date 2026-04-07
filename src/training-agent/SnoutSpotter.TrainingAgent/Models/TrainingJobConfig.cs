namespace SnoutSpotter.TrainingAgent.Models;

public record TrainingJobConfig(
    string JobId,
    string ExportS3Key,
    int Epochs = 100,
    int BatchSize = 16,
    int ImageSize = 640,
    double LearningRate = 0.01,
    int Workers = 8,
    string ModelBase = "yolov8n.pt",
    string? ResumeFrom = null);