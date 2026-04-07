namespace SnoutSpotter.TrainingAgent.Models;

public record TrainingProgress(
    int Epoch,
    int TotalEpochs,
    double? TrainLoss,
    double? ValLoss,
    double? MAP50,
    double? MAP50_95,
    double? BestMAP50,
    long? ElapsedSeconds,
    long? EtaSeconds,
    int? GpuUtilPercent,
    int? GpuTempC);