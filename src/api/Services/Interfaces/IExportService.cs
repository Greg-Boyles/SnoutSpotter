namespace SnoutSpotter.Api.Services.Interfaces;

public interface IExportService
{
    Task<string> TriggerExportAsync(string householdId, int? maxPerClass = null, bool includeBackground = true,
        float backgroundRatio = 1.0f, string exportType = "detection", float cropPadding = 0.1f,
        bool mergeClasses = false);
    Task<List<Dictionary<string, string>>> ListExportsAsync(string householdId);
    Task<string?> GetDownloadUrlAsync(string exportId);
    Task DeleteExportAsync(string exportId);
}
