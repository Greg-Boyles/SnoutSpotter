using System.Collections.Concurrent;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class StatsRefreshService : IStatsRefreshService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonLambda? _lambda;
    private readonly IClipService? _clipService;
    private readonly ILabelService? _labelService;
    private readonly IPiUpdateService? _piUpdateService;
    private readonly AppConfig _config;

    // Per-statId cooldown: don't trigger more than once per 4 minutes from this instance
    private static readonly ConcurrentDictionary<string, DateTime> _lastTriggered = new();

    // Constructor for the API path (read + trigger only)
    [ActivatorUtilitiesConstructor]
    public StatsRefreshService(IAmazonDynamoDB dynamoDb, IAmazonLambda lambda, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _lambda = lambda;
        _config = config.Value;
    }

    // Constructor for the refresh runner path (full compute)
    public StatsRefreshService(
        IAmazonDynamoDB dynamoDb,
        IClipService clipService,
        ILabelService labelService,
        IPiUpdateService piUpdateService,
        IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _clipService = clipService;
        _labelService = labelService;
        _piUpdateService = piUpdateService;
        _config = config.Value;
    }

    public async Task<DashboardStats?> GetCachedDashboardStatsAsync()
    {
        var item = await GetItemAsync("dashboard");
        if (item == null) return null;

        var refreshedAt = item.GetValueOrDefault("refreshed_at")?.S;
        TriggerRefreshIfStale("dashboard", refreshedAt);

        return new DashboardStats(
            TotalClips: int.Parse(item.GetValueOrDefault("total_clips")?.N ?? "0"),
            ClipsToday: int.Parse(item.GetValueOrDefault("clips_today")?.N ?? "0"),
            TotalDetections: int.Parse(item.GetValueOrDefault("total_detections")?.N ?? "0"),
            MyDogDetections: int.Parse(item.GetValueOrDefault("my_dog_detections")?.N ?? "0"),
            LastUploadTime: item.GetValueOrDefault("last_upload_time")?.S,
            PiOnlineCount: int.Parse(item.GetValueOrDefault("pi_online_count")?.N ?? "0"),
            PiTotalCount: int.Parse(item.GetValueOrDefault("pi_total_count")?.N ?? "0"),
            RefreshedAt: refreshedAt);
    }

    public async Task<object?> GetCachedActivityAsync()
    {
        var item = await GetItemAsync("activity");
        if (item == null) return null;

        var refreshedAt = item.GetValueOrDefault("refreshed_at")?.S;
        TriggerRefreshIfStale("activity", refreshedAt);

        var data = item.GetValueOrDefault("data")?.S;
        if (data == null) return null;

        var activity = JsonSerializer.Deserialize<object>(data);
        return new { activity };
    }

    public async Task<object?> GetCachedLabelStatsAsync()
    {
        var item = await GetItemAsync("label_stats");
        if (item == null) return null;

        var refreshedAt = item.GetValueOrDefault("refreshed_at")?.S;
        TriggerRefreshIfStale("label_stats", refreshedAt);

        var data = item.GetValueOrDefault("data")?.S;
        if (data == null) return null;

        var stats = JsonSerializer.Deserialize<object>(data);
        return stats;
    }

    public void TriggerRefreshIfStale(string statId, string? refreshedAt)
    {
        if (_lambda == null) return;

        // Only trigger if stale (older than 5 minutes)
        if (refreshedAt != null &&
            DateTime.TryParse(refreshedAt, out var ts) &&
            (DateTime.UtcNow - ts).TotalMinutes < 5)
            return;

        // Cooldown: don't fire again if we already triggered within the last 4 minutes from this instance
        var now = DateTime.UtcNow;
        if (_lastTriggered.TryGetValue(statId, out var lastFired) && (now - lastFired).TotalMinutes < 4)
            return;

        _lastTriggered[statId] = now;

        // Fire-and-forget — don't await
        _ = _lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = _config.StatsRefreshFunctionName,
            InvocationType = InvocationType.Event
        });
    }

    public async Task RefreshAllAsync()
    {
        if (_clipService == null || _labelService == null || _piUpdateService == null)
            throw new InvalidOperationException("StatsRefreshService not initialised with compute dependencies.");

        var today = DateTime.UtcNow.ToString("yyyy/MM/dd");
        var refreshedAt = DateTime.UtcNow.ToString("O");

        // --- Dashboard stats ---
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
                piOnlineCount++;

            if (shadow?.LastUploadAt != null &&
                (lastUploadAcrossAll == null ||
                 string.Compare(shadow.LastUploadAt, lastUploadAcrossAll, StringComparison.Ordinal) > 0))
                lastUploadAcrossAll = shadow.LastUploadAt;
        }

        var lastUploadTime = lastUploadAcrossAll ?? allClips.Clips.MaxBy(c => c.Timestamp)?.CreatedAt;

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _config.StatsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new AttributeValue { S = "dashboard" },
                ["total_clips"] = new AttributeValue { N = allClips.TotalCount.ToString() },
                ["clips_today"] = new AttributeValue { N = todayClips.TotalCount.ToString() },
                ["total_detections"] = new AttributeValue { N = detections.Count.ToString() },
                ["my_dog_detections"] = new AttributeValue { N = detections.Count(d => d.DetectionType == "my_dog").ToString() },
                ["last_upload_time"] = new AttributeValue { S = lastUploadTime ?? "" },
                ["pi_online_count"] = new AttributeValue { N = piOnlineCount.ToString() },
                ["pi_total_count"] = new AttributeValue { N = thingNames.Count.ToString() },
                ["refreshed_at"] = new AttributeValue { S = refreshedAt }
            }
        });

        // --- Activity (14 days) ---
        var days = 14;
        var activityTasks = Enumerable.Range(0, days)
            .Select(i => DateTime.UtcNow.AddDays(-(days - 1 - i)))
            .Select(async d => new
            {
                date = d.ToString("yyyy-MM-dd"),
                count = await _clipService.GetClipCountForDateAsync(d.ToString("yyyy/MM/dd"))
            });
        var activity = await Task.WhenAll(activityTasks);

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _config.StatsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new AttributeValue { S = "activity" },
                ["data"] = new AttributeValue { S = JsonSerializer.Serialize(activity) },
                ["refreshed_at"] = new AttributeValue { S = refreshedAt }
            }
        });

        // --- Label stats ---
        var labelStats = await _labelService.GetStatsAsync();

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _config.StatsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new AttributeValue { S = "label_stats" },
                ["data"] = new AttributeValue { S = JsonSerializer.Serialize(labelStats) },
                ["refreshed_at"] = new AttributeValue { S = refreshedAt }
            }
        });
    }

    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string statId)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _config.StatsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new AttributeValue { S = statId }
            }
        });
        return response.IsItemSet ? response.Item : null;
    }
}
