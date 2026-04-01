using System.Text.Json;
using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services;

public interface IClipService
{
    Task<ClipListResponse> GetClipsAsync(string? date = null, int limit = 20, string? nextPageKey = null);
    Task<ClipDetail?> GetClipByIdAsync(string clipId);
    Task<List<DetectionSummary>> GetDetectionsAsync(string? detectionType = null, string? dateFrom = null, string? dateTo = null, int limit = 50);
}

public interface IExportService
{
    Task<string> TriggerExportAsync();
    Task<List<Dictionary<string, string>>> ListExportsAsync();
    Task<string?> GetDownloadUrlAsync(string exportId);
    Task DeleteExportAsync(string exportId);
}

public interface IHealthService
{
    Task<bool> IsPiOnlineAsync();
}

public interface ILabelService
{
    Task<object> TriggerAutoLabelAsync(string? date);
    Task<object> GetStatsAsync();
    Task<(List<Dictionary<string, string>> items, string? nextPageKey)> GetLabelsAsync(
        string? reviewed, string? label, int limit, string? nextPageKey);
    Task UpdateLabelAsync(string keyframeKey, string confirmedLabel);
    Task BulkConfirmAsync(List<string> keyframeKeys, string confirmedLabel);
    string GetPresignedUrl(string keyframeKey);
    Task<Dictionary<string, string>> UploadTrainingImageAsync(Stream imageStream, string fileName);
}

public interface ILogService
{
    Task<List<LogEntry>> GetLogsAsync(string thingName, int minutes = 60, string? level = null,
        string? service = null, int limit = 200);
}

public interface IPiUpdateService
{
    Task<List<string>> ListPisAsync();
    Task<PiShadowState?> GetPiShadowAsync(string thingName);
    Task<string?> GetLatestVersionAsync();
    Task<Dictionary<string, string>> UpdateConfigAsync(string thingName, Dictionary<string, JsonElement> changes);
    Task TriggerUpdateAsync(string thingName, string? version = null);
    Task TriggerUpdateAllAsync(string? version = null);
    Task<string> SendCommandAsync(string thingName, string action);
    Task<Dictionary<string, string>?> GetCommandFromLedgerAsync(string commandId);
    Task<List<Dictionary<string, string>>> GetCommandHistoryAsync(string thingName, int limit = 50);
}

public interface IS3PresignService
{
    string GeneratePresignedUrl(string s3Key, int expirySeconds = 3600);
    List<string> GeneratePresignedUrls(IEnumerable<string> s3Keys, int expirySeconds = 3600);
}

public interface IS3UrlService
{
    string GetPresignedUrl(string key, TimeSpan expiration);
    List<string> GetPresignedUrls(IEnumerable<string> keys, TimeSpan expiration);
}

public interface IStreamService
{
    Task<StreamStartResult> StartStreamAsync(string thingName);
    Task<string?> GetHlsUrlAsync(string thingName);
    Task StopStreamAsync(string thingName);
}
