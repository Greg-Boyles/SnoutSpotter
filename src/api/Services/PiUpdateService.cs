using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace SnoutSpotter.Api.Services;

public class PiUpdateService : IPiUpdateService
{
    private readonly IAmazonIoT _iot;
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly AppConfig _config;

    private string? _cachedLatestVersion;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PiUpdateService(IAmazonIoT iot, IAmazonIotData iotData, IAmazonS3 s3Client,
        IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _iot = iot;
        _iotData = iotData;
        _s3Client = s3Client;
        _dynamoDb = dynamoDb;
        _config = config.Value;
    }

    public async Task<List<string>> ListPisAsync()
    {
        try
        {
            var response = await _iot.ListThingsInThingGroupAsync(new ListThingsInThingGroupRequest
            {
                ThingGroupName = _config.IoTThingGroup
            });
            return response.Things;
        }
        catch
        {
            return new List<string>();
        }
    }

    public async Task<PiShadowState?> GetPiShadowAsync(string thingName)
    {
        try
        {
            var response = await _iotData.GetThingShadowAsync(new GetThingShadowRequest
            {
                ThingName = thingName
            });

            using var reader = new StreamReader(response.Payload);
            var json = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(json);

            var reported = doc.RootElement.GetProperty("state").GetProperty("reported");

            var state = new PiShadowState
            {
                ThingName = thingName,
                Version = reported.TryGetProperty("version", out var v) ? v.GetString() : null,
                Hostname = reported.TryGetProperty("hostname", out var h) ? h.GetString() : null,
                LastHeartbeat = reported.TryGetProperty("lastHeartbeat", out var lb) ? lb.GetString() : null,
                UpdateStatus = reported.TryGetProperty("updateStatus", out var us) ? us.GetString() : "idle",
                Services = reported.TryGetProperty("services", out var svc)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(svc.GetRawText())
                    : null,
                LastMotionAt = reported.TryGetProperty("lastMotionAt", out var lm) ? lm.GetString() : null,
                LastUploadAt = reported.TryGetProperty("lastUploadAt", out var lu) ? lu.GetString() : null,
                ClipsPending = reported.TryGetProperty("clipsPending", out var cp) ? cp.GetInt32() : null
            };

            if (reported.TryGetProperty("camera", out var cam))
            {
                state.Camera = new CameraStatus(
                    Connected: cam.TryGetProperty("connected", out var cc) && cc.GetBoolean(),
                    Healthy: cam.TryGetProperty("healthy", out var ch) && ch.GetBoolean(),
                    Sensor: cam.TryGetProperty("sensor", out var cs) ? cs.GetString() : null,
                    Resolution: cam.TryGetProperty("resolution", out var cr) ? cr.GetString() : null,
                    RecordResolution: cam.TryGetProperty("recordResolution", out var crr) ? crr.GetString() : null
                );
            }

            if (reported.TryGetProperty("uploadStats", out var ups))
            {
                state.UploadStats = new UploadStats(
                    UploadsToday: ups.TryGetProperty("uploadsToday", out var ut) ? ut.GetInt32() : 0,
                    FailedToday: ups.TryGetProperty("failedToday", out var ft) ? ft.GetInt32() : 0,
                    TotalUploaded: ups.TryGetProperty("totalUploaded", out var tu) ? tu.GetInt32() : 0
                );
            }

            if (reported.TryGetProperty("system", out var sys))
            {
                state.System = new SystemInfo(
                    CpuTempC: sys.TryGetProperty("cpuTempC", out var ct) ? ct.GetDouble() : null,
                    MemUsedPercent: sys.TryGetProperty("memUsedPercent", out var mu) ? mu.GetDouble() : null,
                    DiskUsedPercent: sys.TryGetProperty("diskUsedPercent", out var du) ? du.GetDouble() : null,
                    DiskFreeGb: sys.TryGetProperty("diskFreeGb", out var df) ? df.GetDouble() : null,
                    UptimeSeconds: sys.TryGetProperty("uptimeSeconds", out var ut2) ? ut2.GetInt64() : null,
                    LoadAvg: sys.TryGetProperty("loadAvg", out var la)
                        ? JsonSerializer.Deserialize<double[]>(la.GetRawText())
                        : null,
                    PiModel: sys.TryGetProperty("piModel", out var pm) ? pm.GetString() : null,
                    IpAddress: sys.TryGetProperty("ipAddress", out var ip) ? ip.GetString() : null,
                    WifiSignalDbm: sys.TryGetProperty("wifiSignalDbm", out var ws) ? ws.GetInt32() : null,
                    WifiSsid: sys.TryGetProperty("wifiSsid", out var wn) ? wn.GetString() : null,
                    PythonVersion: sys.TryGetProperty("pythonVersion", out var pv) ? pv.GetString() : null
                );
            }

            if (reported.TryGetProperty("config", out var cfg))
                state.Config = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(cfg.GetRawText());

            if (reported.TryGetProperty("configErrors", out var ce))
                state.ConfigErrors = JsonSerializer.Deserialize<Dictionary<string, string>>(ce.GetRawText());

            if (reported.TryGetProperty("logShipping", out var ls))
                state.LogShipping = ls.GetBoolean();

            if (reported.TryGetProperty("logShippingError", out var lse))
                state.LogShippingError = lse.GetString();

            if (reported.TryGetProperty("streaming", out var st))
                state.Streaming = st.GetBoolean();

            if (reported.TryGetProperty("streamError", out var se))
                state.StreamError = se.GetString();

            return state;
        }
        catch (Amazon.IotData.Model.ResourceNotFoundException)
        {
            return null; // Shadow doesn't exist yet
        }
    }

    public async Task<string?> GetLatestVersionAsync()
    {
        if (_cachedLatestVersion != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedLatestVersion;

        try
        {
            var response = await _s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _config.BucketName,
                Key = "releases/pi/manifest.json"
            });

            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(json);

            _cachedLatestVersion = doc.RootElement.GetProperty("latest").GetString();
            _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
            return _cachedLatestVersion;
        }
        catch
        {
            return null; // Manifest doesn't exist yet
        }
    }

    private record ConfigKeySpec(string Type, int? Min = null, int? Max = null, bool Odd = false, string[]? Choices = null);

    private static readonly Dictionary<string, ConfigKeySpec> ConfigurableKeys = new()
    {
        ["motion.threshold"]                    = new("int", Min: 500,  Max: 50000),
        ["motion.blur_kernel"]                  = new("int", Min: 3,    Max: 51,   Odd: true),
        ["camera.detection_fps"]                = new("int", Min: 1,    Max: 15),
        ["recording.max_clip_length"]           = new("int", Min: 10,   Max: 300),
        ["recording.pre_buffer"]                = new("int", Min: 1,    Max: 10),
        ["recording.pre_buffer_enabled"]        = new("bool"),
        ["recording.post_motion_buffer"]        = new("int", Min: 3,    Max: 60),
        ["upload.max_retries"]                  = new("int", Min: 1,    Max: 20),
        ["upload.delete_after_upload"]          = new("bool"),
        ["health.interval_seconds"]             = new("int", Min: 60,   Max: 3600),
        ["log_shipping.enabled"]                = new("bool"),
        ["log_shipping.batch_interval_seconds"] = new("int", Min: 30,   Max: 600),
        ["log_shipping.max_lines_per_batch"]    = new("int", Min: 10,   Max: 200),
        ["log_shipping.min_level"]              = new("str", Choices: ["DEBUG", "INFO", "WARNING", "ERROR"]),
        ["credentials_provider.endpoint"]       = new("str"),
    };

    public async Task<Dictionary<string, string>> UpdateConfigAsync(
        string thingName, Dictionary<string, JsonElement> changes)
    {
        var errors = new Dictionary<string, string>();
        var valid = new Dictionary<string, JsonElement>();

        foreach (var (key, value) in changes)
        {
            if (!ConfigurableKeys.TryGetValue(key, out var spec))
            {
                errors[key] = $"Unknown config key: {key}";
                continue;
            }

            if (spec.Type == "bool")
            {
                if (value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                    errors[key] = $"{key} must be a boolean";
                else
                    valid[key] = value;
            }
            else if (spec.Type == "str")
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    errors[key] = $"{key} must be a string";
                }
                else if (spec.Choices != null && !spec.Choices.Contains(value.GetString()))
                {
                    errors[key] = $"{key} must be one of: {string.Join(", ", spec.Choices)}";
                }
                else
                {
                    valid[key] = value;
                }
            }
            else // int
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var intVal))
                {
                    errors[key] = $"{key} must be an integer";
                }
                else if (spec.Min.HasValue && intVal < spec.Min.Value)
                {
                    errors[key] = $"{key} must be >= {spec.Min}";
                }
                else if (spec.Max.HasValue && intVal > spec.Max.Value)
                {
                    errors[key] = $"{key} must be <= {spec.Max}";
                }
                else if (spec.Odd && intVal % 2 == 0)
                {
                    errors[key] = $"{key} must be an odd number";
                }
                else
                {
                    valid[key] = value;
                }
            }
        }

        if (valid.Count > 0)
        {
            var payload = JsonSerializer.Serialize(new
            {
                state = new { desired = new { config = valid } }
            });

            await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
            {
                ThingName = thingName,
                Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
            });
        }

        return errors;
    }

    public async Task TriggerUpdateAsync(string thingName, string? version = null)
    {
        version ??= await GetLatestVersionAsync()
            ?? throw new InvalidOperationException("No version available for update");

        var payload = JsonSerializer.Serialize(new
        {
            state = new
            {
                desired = new { version }
            }
        });

        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
        });
    }

    public async Task TriggerUpdateAllAsync(string? version = null)
    {
        var things = await ListPisAsync();
        version ??= await GetLatestVersionAsync()
            ?? throw new InvalidOperationException("No version available for update");

        var tasks = things.Select(thing => TriggerUpdateAsync(thing, version));
        await Task.WhenAll(tasks);
    }

    private static readonly HashSet<string> AllowedCommands = new()
    {
        "restart-motion", "restart-uploader", "restart-agent",
        "reboot", "clear-clips", "clear-backups"
    };

    public async Task<string> SendCommandAsync(string thingName, string action)
    {
        if (!AllowedCommands.Contains(action))
            throw new ArgumentException($"Unknown command: {action}");

        var commandId = Guid.NewGuid().ToString("N");
        var requestedAt = DateTime.UtcNow.ToString("O");
        var ttl = DateTimeOffset.UtcNow.AddDays(14).ToUnixTimeSeconds();

        // Write command to DynamoDB ledger
        await _dynamoDb.PutItemAsync(_config.CommandsTable,
            new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["command_id"] = new() { S = commandId },
                ["thing_name"] = new() { S = thingName },
                ["action"] = new() { S = action },
                ["status"] = new() { S = "sent" },
                ["requested_at"] = new() { S = requestedAt },
                ["ttl"] = new() { N = ttl.ToString() },
            });

        // Publish command via MQTT
        var payload = JsonSerializer.Serialize(new
        {
            command_id = commandId,
            action,
            requestedAt
        });

        var topic = $"snoutspotter/{thingName}/commands";
        await _iotData.PublishAsync(new Amazon.IotData.Model.PublishRequest
        {
            Topic = topic,
            Qos = 1,
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
        });

        return commandId;
    }

    public async Task<Dictionary<string, string>?> GetCommandFromLedgerAsync(string commandId)
    {
        var result = await _dynamoDb.GetItemAsync(_config.CommandsTable,
            new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                ["command_id"] = new() { S = commandId }
            });

        if (!result.IsItemSet) return null;

        var item = result.Item;
        var dict = new Dictionary<string, string>();
        foreach (var (k, v) in item)
        {
            if (v.S != null) dict[k] = v.S;
            else if (v.N != null) dict[k] = v.N;
        }
        return dict;
    }

    public async Task<List<Dictionary<string, string>>> GetCommandHistoryAsync(string thingName, int limit = 50)
    {
        var result = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _config.CommandsTable,
            IndexName = "by-device",
            KeyConditionExpression = "thing_name = :tn",
            ExpressionAttributeValues = new Dictionary<string, Amazon.DynamoDBv2.Model.AttributeValue>
            {
                [":tn"] = new() { S = thingName }
            },
            ScanIndexForward = false,
            Limit = limit
        });

        return result.Items.Select(item =>
        {
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in item)
            {
                if (v.S != null) dict[k] = v.S;
                else if (v.N != null) dict[k] = v.N;
            }
            return dict;
        }).ToList();
    }
}

public class PiShadowState
{
    public string ThingName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Hostname { get; set; }
    public string? LastHeartbeat { get; set; }
    public string? UpdateStatus { get; set; }
    public Dictionary<string, string>? Services { get; set; }
    public CameraStatus? Camera { get; set; }
    public string? LastMotionAt { get; set; }
    public string? LastUploadAt { get; set; }
    public UploadStats? UploadStats { get; set; }
    public int? ClipsPending { get; set; }
    public SystemInfo? System { get; set; }
    public Dictionary<string, JsonElement>? Config { get; set; }
    public Dictionary<string, string>? ConfigErrors { get; set; }
    public bool? LogShipping { get; set; }
    public string? LogShippingError { get; set; }
    public bool? Streaming { get; set; }
    public string? StreamError { get; set; }
}

public record CameraStatus(
    bool Connected,
    bool Healthy,
    string? Sensor,
    string? Resolution,
    string? RecordResolution
);

public record UploadStats(
    int UploadsToday,
    int FailedToday,
    int TotalUploaded
);

public record SystemInfo(
    double? CpuTempC,
    double? MemUsedPercent,
    double? DiskUsedPercent,
    double? DiskFreeGb,
    long? UptimeSeconds,
    double[]? LoadAvg,
    string? PiModel,
    string? IpAddress,
    int? WifiSignalDbm,
    string? WifiSsid,
    string? PythonVersion
);
