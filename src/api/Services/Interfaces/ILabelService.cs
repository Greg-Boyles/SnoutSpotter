namespace SnoutSpotter.Api.Services.Interfaces;

public interface ILabelService
{
    Task<object> TriggerAutoLabelAsync(string? date);
    Task<object> GetStatsAsync();
    Task<(List<Dictionary<string, string>> items, string? nextPageKey)> GetLabelsAsync(
        string? reviewed, string? label, string? confirmedLabel, string? breed, string? device, int limit, string? nextPageKey);
    Task UpdateLabelAsync(string keyframeKey, string confirmedLabel, string? breed = null);
    Task BulkConfirmAsync(List<string> keyframeKeys, string confirmedLabel, string? breed = null);
    string GetPresignedUrl(string keyframeKey);
    Task<Dictionary<string, string>> UploadTrainingImageAsync(Stream imageStream, string fileName, string confirmedLabel, string? breed = null);
    Task<int> BackfillBreedAsync(string confirmedLabel, string breed);
    Task<object> BackfillBoundingBoxesAsync(string? confirmedLabel, List<string>? keys = null);
    Task<Dictionary<string, string?>?> GetLabelAsync(string keyframeKey);
}
