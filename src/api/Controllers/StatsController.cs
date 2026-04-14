using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StatsController : ControllerBase
{
    private readonly IClipService _clipService;
    private readonly IPiUpdateService _piUpdateService;
    private readonly IStatsRefreshService _statsCache;
    private readonly AppConfig _config;

    public StatsController(IClipService clipService, IPiUpdateService piUpdateService, IStatsRefreshService statsCache, IOptions<AppConfig> config)
    {
        _clipService = clipService;
        _piUpdateService = piUpdateService;
        _statsCache = statsCache;
        _config = config.Value;
    }

    [HttpGet]
    public async Task<ActionResult<DashboardStats>> GetStats()
    {
        var cached = await _statsCache.GetCachedDashboardStatsAsync();
        if (cached != null) return Ok(cached);

        var today = DateTime.UtcNow.ToString("yyyy/MM/dd");

        var allClips = await _clipService.GetClipsAsync(limit: 1000);
        var todayClips = await _clipService.GetClipsAsync(date: today, limit: 1000);
        var detections = await _clipService.GetDetectionsAsync(limit: 1000);

        var thingNames = await _piUpdateService.ListPisAsync();
        var piOnlineCount = 0;
        string? lastUploadAcrossAll = null;

        foreach (var thingName in thingNames)
        {
            var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
            if (shadow?.LastHeartbeat != null &&
                DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
                (DateTime.UtcNow - lastHb).TotalMinutes < 5)
            {
                piOnlineCount++;
            }

            if (shadow?.LastUploadAt != null &&
                (lastUploadAcrossAll == null || string.Compare(shadow.LastUploadAt, lastUploadAcrossAll, StringComparison.Ordinal) > 0))
            {
                lastUploadAcrossAll = shadow.LastUploadAt;
            }
        }

        var lastUploadTime = lastUploadAcrossAll ?? allClips.Clips.MaxBy(c => c.Timestamp)?.CreatedAt;

        var stats = new DashboardStats(
            TotalClips: allClips.TotalCount,
            ClipsToday: todayClips.TotalCount,
            TotalDetections: detections.Count,
            MyDogDetections: detections.Count(d => d.DetectionType == "my_dog"),
            LastUploadTime: lastUploadTime,
            PiOnlineCount: piOnlineCount,
            PiTotalCount: thingNames.Count);

        return Ok(stats);
    }

    [HttpGet("activity")]
    public async Task<ActionResult<object>> GetActivity([FromQuery] int days = 14)
    {
        if (days == 14)
        {
            var cached = await _statsCache.GetCachedActivityAsync();
            if (cached != null) return Ok(cached);
        }

        var tasks = Enumerable.Range(0, days)
            .Select(i => DateTime.UtcNow.AddDays(-(days - 1 - i)))
            .Select(async d => new
            {
                date = d.ToString("yyyy-MM-dd"),
                count = await _clipService.GetClipCountForDateAsync(d.ToString("yyyy/MM/dd"))
            });

        var activity = await Task.WhenAll(tasks);
        return Ok(new { activity });
    }

    [HttpGet("health")]
    public async Task<ActionResult<object>> GetHealth()
    {
        var thingNames = await _piUpdateService.ListPisAsync();
        var latestVersion = await _piUpdateService.GetLatestVersionAsync();

        var devices = new List<object>();
        foreach (var thingName in thingNames)
        {
            var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
            var piOnline = shadow?.LastHeartbeat != null &&
                DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
                (DateTime.UtcNow - lastHb).TotalMinutes < 5;

            devices.Add(new
            {
                thingName,
                online = piOnline,
                version = shadow?.Version,
                hostname = shadow?.Hostname,
                lastHeartbeat = shadow?.LastHeartbeat,
                updateStatus = shadow?.UpdateStatus ?? "idle",
                services = shadow?.Services,
                camera = shadow?.Camera,
                lastMotionAt = shadow?.LastMotionAt,
                lastUploadAt = shadow?.LastUploadAt,
                uploadStats = shadow?.UploadStats,
                clipsPending = shadow?.ClipsPending,
                system = shadow?.System,
                updateAvailable = latestVersion != null && shadow?.Version != null && latestVersion != shadow.Version
            });
        }

        return Ok(new
        {
            checkedAt = DateTime.UtcNow.ToString("O"),
            latestVersion,
            devices
        });
    }

    [HttpGet("queues")]
    public async Task<ActionResult> GetQueueStats()
    {
        var queues = new (string Name, string Url)[]
        {
            ("Backfill Boxes", _config.BackfillQueueUrl),
            ("Rerun Inference", _config.RerunInferenceQueueUrl),
            ("Training Jobs", _config.TrainingJobQueueUrl),
        };

        using var sqsClient = new AmazonSQSClient();
        var results = new List<object>();

        foreach (var (name, url) in queues)
        {
            if (string.IsNullOrEmpty(url)) continue;

            try
            {
                var attrs = await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
                {
                    QueueUrl = url,
                    AttributeNames = new List<string>
                    {
                        "ApproximateNumberOfMessages",
                        "ApproximateNumberOfMessagesNotVisible"
                    }
                });

                var pending = int.Parse(attrs.Attributes.GetValueOrDefault("ApproximateNumberOfMessages", "0"));
                var inFlight = int.Parse(attrs.Attributes.GetValueOrDefault("ApproximateNumberOfMessagesNotVisible", "0"));

                // Derive DLQ URL: strip -queue suffix if present, add -dlq
                var dlqPending = 0;
                var queueName = url.Split('/').Last();
                var baseName = queueName.EndsWith("-queue") ? queueName[..^6] : queueName;
                var dlqUrl = url[..^queueName.Length] + baseName + "-dlq";
                try
                {
                    var dlqAttrs = await sqsClient.GetQueueAttributesAsync(new GetQueueAttributesRequest
                    {
                        QueueUrl = dlqUrl,
                        AttributeNames = new List<string> { "ApproximateNumberOfMessages" }
                    });
                    dlqPending = int.Parse(dlqAttrs.Attributes.GetValueOrDefault("ApproximateNumberOfMessages", "0"));
                }
                catch { /* DLQ may not exist or URL pattern different */ }

                results.Add(new { name, pending, inFlight, dlqPending });
            }
            catch
            {
                results.Add(new { name, pending = -1, inFlight = -1, dlqPending = -1 });
            }
        }

        return Ok(new { queues = results });
    }
}
