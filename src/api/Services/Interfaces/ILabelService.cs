namespace SnoutSpotter.Api.Services;

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
