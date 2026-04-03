namespace SnoutSpotter.Api.Services.Interfaces;

public interface ILogService
{
    Task<List<LogEntry>> GetLogsAsync(string thingName, int minutes = 60, string? level = null,
        string? service = null, int limit = 200);
}
