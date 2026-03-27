using System.Text.Json;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.S3;
using Amazon.S3.Model;

namespace SnoutSpotter.Api.Services;

public class PiUpdateService
{
    private readonly IAmazonIoT _iot;
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly string _thingGroupName;

    private string? _cachedLatestVersion;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PiUpdateService(IAmazonIoT iot, IAmazonIotData iotData, IAmazonS3 s3Client, IConfiguration configuration)
    {
        _iot = iot;
        _iotData = iotData;
        _s3Client = s3Client;
        _bucketName = configuration["BUCKET_NAME"]
            ?? throw new InvalidOperationException("BUCKET_NAME not configured");
        _thingGroupName = configuration["IOT_THING_GROUP"] ?? "snoutspotter-pis";
    }

    public async Task<List<string>> ListPisAsync()
    {
        try
        {
            var response = await _iot.ListThingsInThingGroupAsync(new ListThingsInThingGroupRequest
            {
                ThingGroupName = _thingGroupName
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

            return new PiShadowState
            {
                ThingName = thingName,
                Version = reported.TryGetProperty("version", out var v) ? v.GetString() : null,
                Hostname = reported.TryGetProperty("hostname", out var h) ? h.GetString() : null,
                LastHeartbeat = reported.TryGetProperty("lastHeartbeat", out var lb) ? lb.GetString() : null,
                UpdateStatus = reported.TryGetProperty("updateStatus", out var us) ? us.GetString() : "idle",
                Services = reported.TryGetProperty("services", out var svc)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(svc.GetRawText())
                    : null
            };
        }
        catch (ResourceNotFoundException)
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
                BucketName = _bucketName,
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
}

public class PiShadowState
{
    public string ThingName { get; set; } = string.Empty;
    public string? Version { get; set; }
    public string? Hostname { get; set; }
    public string? LastHeartbeat { get; set; }
    public string? UpdateStatus { get; set; }
    public Dictionary<string, string>? Services { get; set; }
}
