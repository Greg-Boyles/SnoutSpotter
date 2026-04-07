namespace SnoutSpotter.TrainingAgent.Models;

public record TrainingResult(
    string ModelS3Key,
    double ModelSizeMb,
    double FinalMAP50,
    double? FinalMAP50_95,
    double? Precision,
    double? Recall,
    string[] Classes,
    int TotalEpochs,
    int BestEpoch,
    long TrainingTimeSeconds,
    int DatasetImages);