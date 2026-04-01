using System.Text.Json;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Microsoft.Extensions.Options;

namespace SnoutSpotter.Api.Services;

public class LogService : ILogService
{
    private readonly IAmazonCloudWatchLogs _logsClient;
    private readonly string _logGroupName;

    public LogService(IAmazonCloudWatchLogs logsClient, IOptions<AppConfig> config)
    {
        _logsClient = logsClient;
        _logGroupName = config.Value.PiLogGroup;
    }

    public async Task<List<LogEntry>> GetLogsAsync(
        string thingName,
        int minutes = 60,
        string? levelFilter = null,
        string? serviceFilter = null,
        int limit = 200)
    {
        var query = BuildQuery(thingName, levelFilter, serviceFilter, limit);

        try
        {
            var startResponse = await _logsClient.StartQueryAsync(new StartQueryRequest
            {
                LogGroupName = _logGroupName,
                StartTime = DateTimeOffset.UtcNow.AddMinutes(-minutes).ToUnixTimeSeconds(),
                EndTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                QueryString = query,
                Limit = limit
            });

            var queryId = startResponse.QueryId;
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (DateTime.UtcNow < deadline)
            {
                var results = await _logsClient.GetQueryResultsAsync(new GetQueryResultsRequest
                {
                    QueryId = queryId
                });

                if (results.Status == QueryStatus.Complete)
                    return ParseResults(results.Results, limit);

                if (results.Status == QueryStatus.Failed || results.Status == QueryStatus.Cancelled)
                    break;

                await Task.Delay(500);
            }
        }
        catch (ResourceNotFoundException)
        {
            // Log stream doesn't exist yet (new device, no logs shipped yet)
        }

        return new List<LogEntry>();
    }

    private static string BuildQuery(string thingName, string? levelFilter, string? serviceFilter, int limit)
    {
        // Lambda writes individual JSON log events to per-device streams:
        // {"ts": "...", "level": "INFO", "service": "motion", "msg": "..."}
        var query = $"fields @timestamp, @message | filter @logStream = '{thingName}'";

        if (levelFilter != null)
            query += $" | filter @message like /\"level\":\"{levelFilter}\"/";

        if (serviceFilter != null)
            query += $" | filter @message like /\"service\":\"[^\"]*{serviceFilter}/";

        query += $" | sort @timestamp desc | limit {limit}";

        return query;
    }

    private static List<LogEntry> ParseResults(List<List<ResultField>> results, int limit)
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

                var ts = root.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "";
                var level = root.TryGetProperty("level", out var l) ? l.GetString() ?? "" : "";
                var service = root.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
                var msg = root.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";

                entries.Add(new LogEntry(ts, level, service, msg));

                if (entries.Count >= limit)
                    return entries;
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
