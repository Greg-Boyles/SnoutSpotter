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
    private readonly IAmazonLambda _lambda;
    private readonly AppConfig _config;

    // Per-statId cooldown: don't trigger more than once per 4 minutes from this instance
    private static readonly ConcurrentDictionary<string, DateTime> _lastTriggered = new();

    [ActivatorUtilitiesConstructor]
    public StatsRefreshService(IAmazonDynamoDB dynamoDb, IAmazonLambda lambda, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _lambda = lambda;
        _config = config.Value;
    }

    public async Task<DashboardStats?> GetCachedDashboardStatsAsync()
    {
        var item = await GetItemAsync("dashboard");
        if (item == null)
        {
            await InvokeRefreshAsync(waitForResult: true);
            item = await GetItemAsync("dashboard");
            if (item == null) return null;
        }

        var refreshedAt = item.GetValueOrDefault("refreshed_at")?.S;
        TriggerIfStale("dashboard", refreshedAt);

        var myDogDetections = int.Parse(item.GetValueOrDefault("my_dog_detections")?.N ?? "0");
        var knownPetDetections = int.Parse(item.GetValueOrDefault("known_pet_detections")?.N ?? "0");

        // Parse per-pet detection counts from DynamoDB map
        Dictionary<string, int>? petDetectionCounts = null;
        if (item.TryGetValue("pet_detection_counts", out var petCountsAttr) && petCountsAttr.M?.Count > 0)
        {
            petDetectionCounts = petCountsAttr.M
                .ToDictionary(kv => kv.Key, kv => int.Parse(kv.Value.N ?? "0"));
        }

        // Fallback: if no known_pet_detections yet, use my_dog_detections
        if (knownPetDetections == 0 && myDogDetections > 0)
            knownPetDetections = myDogDetections;

        return new DashboardStats(
            TotalClips: int.Parse(item.GetValueOrDefault("total_clips")?.N ?? "0"),
            ClipsToday: int.Parse(item.GetValueOrDefault("clips_today")?.N ?? "0"),
            TotalDetections: int.Parse(item.GetValueOrDefault("total_detections")?.N ?? "0"),
            MyDogDetections: myDogDetections,
            KnownPetDetections: knownPetDetections,
            PetDetectionCounts: petDetectionCounts,
            LastUploadTime: item.GetValueOrDefault("last_upload_time")?.S,
            PiOnlineCount: int.Parse(item.GetValueOrDefault("pi_online_count")?.N ?? "0"),
            PiTotalCount: int.Parse(item.GetValueOrDefault("pi_total_count")?.N ?? "0"),
            RefreshedAt: refreshedAt);
    }

    public async Task<object?> GetCachedActivityAsync()
    {
        var item = await GetItemAsync("activity");
        if (item == null)
        {
            await InvokeRefreshAsync(waitForResult: true);
            item = await GetItemAsync("activity");
            if (item == null) return null;
        }

        var refreshedAt = item.GetValueOrDefault("refreshed_at")?.S;
        TriggerIfStale("activity", refreshedAt);

        var data = item.GetValueOrDefault("data")?.S;
        if (data == null) return null;

        var activity = JsonSerializer.Deserialize<object>(data);
        return new { activity };
    }

    public async Task<object?> GetCachedLabelStatsAsync()
    {
        var item = await GetItemAsync("label_stats");
        if (item == null)
        {
            await InvokeRefreshAsync(waitForResult: true);
            item = await GetItemAsync("label_stats");
            if (item == null) return null;
        }

        var refreshedAt = item.GetValueOrDefault("refreshed_at")?.S;
        TriggerIfStale("label_stats", refreshedAt);

        var data = item.GetValueOrDefault("data")?.S;
        if (data == null) return null;

        return JsonSerializer.Deserialize<object>(data);
    }

    // Called on stale data — fire-and-forget, respects per-instance cooldown
    public void TriggerRefreshIfStale(string statId, string? refreshedAt)
        => TriggerIfStale(statId, refreshedAt);

    private void TriggerIfStale(string statId, string? refreshedAt)
    {
        // Only trigger if stale (older than 5 minutes)
        if (refreshedAt != null &&
            DateTime.TryParse(refreshedAt, out var ts) &&
            (DateTime.UtcNow - ts).TotalMinutes < 5)
            return;

        // Cooldown: don't fire again within 4 minutes from this instance
        var now = DateTime.UtcNow;
        if (_lastTriggered.TryGetValue(statId, out var lastFired) && (now - lastFired).TotalMinutes < 4)
            return;

        _lastTriggered[statId] = now;

        // Fire-and-forget
        _ = _lambda.InvokeAsync(new InvokeRequest
        {
            FunctionName = _config.StatsRefreshFunctionName,
            InvocationType = InvocationType.Event
        });
    }

    // Synchronous invocation — waits for the stats-refresh Lambda to complete
    private async Task InvokeRefreshAsync(bool waitForResult)
    {
        try
        {
            await _lambda.InvokeAsync(new InvokeRequest
            {
                FunctionName = _config.StatsRefreshFunctionName,
                InvocationType = waitForResult ? InvocationType.RequestResponse : InvocationType.Event
            });
        }
        catch
        {
            // Best-effort — callers fall back to live query or return null
        }
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
