using System.Text.Json;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;

namespace SnoutSpotter.Api.Services;

public class LogService
{
    private readonly IAmazonCloudWatchLogs _logsClient;
    private readonly string _logGroupName;

    public LogService(IAmazonCloudWatchLogs logsClient, IConfiguration configuration)
    {
        _logsClient = logsClient;
        _logGroupName = configuration["PI_LOG_GROUP"] ?? "/snoutspotter/pi-logs";
    }

    public async Task<List<LogEntry>> GetLogsAsync(
        string thingName,
        int minutes = 60,
        string? levelFilter = null,
        string? serviceFilter = null,
        int limit = 200)
    {
        var query = BuildQuery(thingName, levelFilter, serviceFilter, limit);

        var startResponse = await _logsClient.StartQueryAsync(new StartQueryRequest
        {
            LogGroupName = _logGroupName,
            StartTime = DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds(),
            EndTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            QueryString = query,
            Limit = limit
        });

        // Poll for results with timeout
        var queryId = startResponse.QueryId;
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            var results = await _logsClient.GetQueryResultsAsync(new GetQueryResultsRequest
            {
                QueryId = queryId
            });

            if (results.Status == QueryStatus.Complete)
                return ParseResults(results.Results, thingName, levelFilter, serviceFilter, limit);

            if (results.Status == QueryStatus.Failed || results.Status == QueryStatus.Cancelled)
                break;

            await Task.Delay(500);
        }

        return new List<LogEntry>();
    }

    private static string BuildQuery(string thingName, string? levelFilter, string? serviceFilter, int limit)
    {
        // IoT Rule writes the full MQTT JSON payload as @message
        // We filter by thingName in the JSON and sort by ingestion time
        return $@"fields @timestamp, @message
            | sort @timestamp desc
            | limit {limit}";
    }

    private static List<LogEntry> ParseResults(
        List<List<ResultField>> results,
        string thingName,
        string? levelFilter,
        string? serviceFilter,
        int limit)
    {
        var entries = new List<LogEntry>();

        foreach (var row in results)
        {
            var message = row.FirstOrDefault(f => f.Field == "@message")?.Value;
            if (string.IsNullOrEmpty(message))
                continue;

            try
            {
                var doc = JsonDocument.Parse(message);
                var root = doc.RootElement;

                // Filter by thingName
                if (root.TryGetProperty("thingName", out var tn) && tn.GetString() != thingName)
                    continue;

                if (!root.TryGetProperty("logs", out var logsArray))
                    continue;

                foreach (var logEntry in logsArray.EnumerateArray())
                {
                    var level = logEntry.TryGetProperty("level", out var l) ? l.GetString() ?? "" : "";
                    var service = logEntry.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
                    var ts = logEntry.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "";
                    var msg = logEntry.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";

                    if (levelFilter != null && !string.Equals(level, levelFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (serviceFilter != null && !service.Contains(serviceFilter, StringComparison.OrdinalIgnoreCase))
                        continue;

                    entries.Add(new LogEntry(ts, level, service, msg));

                    if (entries.Count >= limit)
                        return entries;
                }
            }
            catch (JsonException)
            {
                // Skip malformed messages
            }
        }

        return entries;
    }
}

public record LogEntry(string Timestamp, string Level, string Service, string Message);
