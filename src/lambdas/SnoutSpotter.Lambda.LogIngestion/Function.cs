using System.Text.Json;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.LogIngestion;

public class Function
{
    private readonly IAmazonCloudWatchLogs _logsClient;
    private readonly string _logGroupName;
    private readonly HashSet<string> _knownStreams = new();

    public Function()
    {
        _logsClient = new AmazonCloudWatchLogsClient();
        _logGroupName = Environment.GetEnvironmentVariable("LOG_GROUP_NAME")
            ?? throw new System.InvalidOperationException("LOG_GROUP_NAME not set");
    }

    public Function(IAmazonCloudWatchLogs logsClient, string logGroupName)
    {
        _logsClient = logsClient;
        _logGroupName = logGroupName;
    }

    public async Task FunctionHandler(JsonElement mqttPayload, ILambdaContext context)
    {
        string? thingName = null;

        try
        {
            if (!mqttPayload.TryGetProperty("thingName", out var tn))
            {
                context.Logger.LogWarning("MQTT payload missing thingName, skipping");
                return;
            }
            thingName = tn.GetString();
            if (string.IsNullOrEmpty(thingName))
                return;

            if (!mqttPayload.TryGetProperty("logs", out var logsArray))
            {
                context.Logger.LogWarning($"No logs array in payload from {thingName}");
                return;
            }

            await EnsureLogStreamExists(thingName, context);

            var logEvents = new List<InputLogEvent>();
            foreach (var entry in logsArray.EnumerateArray())
            {
                var ts = entry.TryGetProperty("ts", out var t) ? t.GetString() ?? "" : "";
                var level = entry.TryGetProperty("level", out var l) ? l.GetString() ?? "" : "";
                var service = entry.TryGetProperty("service", out var s) ? s.GetString() ?? "" : "";
                var msg = entry.TryGetProperty("msg", out var m) ? m.GetString() ?? "" : "";

                var timestamp = ParseTimestamp(ts);

                var message = JsonSerializer.Serialize(new { ts, level, service, msg });
                logEvents.Add(new InputLogEvent
                {
                    Timestamp = timestamp,
                    Message = message
                });
            }

            if (logEvents.Count == 0)
                return;

            // CloudWatch requires events sorted by timestamp
            logEvents.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

            await _logsClient.PutLogEventsAsync(new PutLogEventsRequest
            {
                LogGroupName = _logGroupName,
                LogStreamName = thingName,
                LogEvents = logEvents
            });

            context.Logger.LogInformation($"Wrote {logEvents.Count} log events to stream {thingName}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to process logs for {thingName ?? "unknown"}: {ex.Message}");
            throw;
        }
    }

    private async Task EnsureLogStreamExists(string streamName, ILambdaContext context)
    {
        if (_knownStreams.Contains(streamName))
            return;

        try
        {
            await _logsClient.CreateLogStreamAsync(new CreateLogStreamRequest
            {
                LogGroupName = _logGroupName,
                LogStreamName = streamName
            });
            context.Logger.LogInformation($"Created log stream: {streamName}");
        }
        catch (ResourceAlreadyExistsException)
        {
            // Stream already exists — expected for existing devices
        }

        _knownStreams.Add(streamName);
    }

    private static DateTime ParseTimestamp(string ts)
    {
        if (DateTimeOffset.TryParse(ts, out var dto) && dto.Year >= 2020)
            return dto.UtcDateTime;

        return DateTime.UtcNow;
    }
}
