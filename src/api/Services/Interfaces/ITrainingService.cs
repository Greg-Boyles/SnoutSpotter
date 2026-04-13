using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface ITrainingService
{
    Task<List<TrainerAgentSummary>> ListAgentsAsync();
    Task<object?> GetAgentStatusAsync(string thingName);
    Task TriggerAgentUpdateAsync(string thingName, string version);
    Task<string> SubmitJobAsync(TrainingJobRequest request);
    Task<List<TrainingJobSummary>> ListJobsAsync(string? status = null, int limit = 50);
    Task<TrainingJobDetail?> GetJobAsync(string jobId);
    Task CancelJobAsync(string jobId);
    Task DeleteJobAsync(string jobId);
}

public record TrainerAgentSummary(
    string ThingName,
    bool Online,
    string? Version,
    string? MlScriptVersion,
    string? Hostname,
    string? LastHeartbeat,
    string? CurrentJobId,
    string? Status,
    TrainerGpuSummary? Gpu,
    TrainerProgressSummary? CurrentJobProgress);

public record TrainerGpuSummary(string Name, int VramMb, int TemperatureC, int UtilizationPercent);

public record TrainerProgressSummary(int Epoch, int TotalEpochs, double? MAP50);

public record TrainingJobRequest(
    string ExportId,
    string ExportS3Key,
    int Epochs = 100,
    int BatchSize = 16,
    int ImageSize = 640,
    double LearningRate = 0.01,
    int Workers = 8,
    string ModelBase = "yolov8n.pt",
    string? ResumeFrom = null,
    string? Notes = null,
    string JobType = "detector");

public record TrainingJobSummary(
    string JobId,
    string Status,
    string? AgentThingName,
    string? ExportId,
    int? Epochs,
    string? CreatedAt,
    string? StartedAt,
    string? CompletedAt,
    double? FinalMAP50,
    string JobType = "detector");

public record TrainingJobDetail(
    string JobId,
    string Status,
    string? AgentThingName,
    string? ExportId,
    string? ExportS3Key,
    TrainingJobParams? Config,
    TrainingProgress? Progress,
    TrainingResult? Result,
    string? CheckpointS3Key,
    string? Error,
    string? FailedStage,
    string? CreatedAt,
    string? StartedAt,
    string? CompletedAt,
    string JobType = "detector");
