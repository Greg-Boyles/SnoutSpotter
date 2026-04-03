namespace SnoutSpotter.Api.Services.Interfaces;

public interface IExportService
{
    Task<string> TriggerExportAsync();
    Task<List<Dictionary<string, string>>> ListExportsAsync();
    Task<string?> GetDownloadUrlAsync(string exportId);
    Task DeleteExportAsync(string exportId);
}
