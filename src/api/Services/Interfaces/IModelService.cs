namespace SnoutSpotter.Api.Services.Interfaces;

public interface IModelService
{
    Task<(string? ActiveVersion, List<ModelRecord> Versions)> ListModelsAsync(string type);
    Task<ModelRecord?> GetModelAsync(string type, string version);
    Task<ModelRecord> RegisterModelAsync(RegisterModelRequest request);
    Task ActivateModelAsync(string type, string version);
    Task DeleteModelAsync(string type, string version);
    Task<(string UploadUrl, string S3Key)> GetUploadUrlAsync(string type, string version);
}

public record ModelRecord(
    string ModelId,
    string ModelType,
    string Version,
    string S3Key,
    long SizeBytes,
    string Status,
    string CreatedAt,
    string Source,
    string? TrainingJobId = null,
    string? ExportId = null,
    string? Notes = null,
    Dictionary<string, double>? Metrics = null);

public record RegisterModelRequest(
    string ModelType,
    string Version,
    string S3Key,
    long SizeBytes,
    string Source,
    string? TrainingJobId = null,
    string? ExportId = null,
    string? Notes = null,
    Dictionary<string, double>? Metrics = null);
