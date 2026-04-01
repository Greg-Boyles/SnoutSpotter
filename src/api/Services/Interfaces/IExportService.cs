namespace SnoutSpotter.Api.Services;

public interface IExportService
{
    Task<string> TriggerExportAsync();
    Task<List<Dictionary<string, string>>> ListExportsAsync();
    Task<string?> GetDownloadUrlAsync(string exportId);
    Task DeleteExportAsync(string exportId);
}
