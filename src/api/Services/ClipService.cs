using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services;

public class ClipService : IClipService
{
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly IS3UrlService _s3UrlService;
    private const string TableName = "snout-spotter-clips";

    public ClipService(IAmazonDynamoDB dynamoClient, IS3UrlService s3UrlService)
    {
        _dynamoClient = dynamoClient;
        _s3UrlService = s3UrlService;
    }

    public async Task<ClipListResponse> GetClipsAsync(string? date = null, string? device = null, int limit = 20, string? nextPageKey = null)
    {
        if (date != null)
        {
            return await QueryByDateAsync(date, device, limit, nextPageKey);
        }

        string? filterExpression = null;
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":pk"] = new() { S = "CLIP" }
        };

        if (!string.IsNullOrEmpty(device))
        {
            filterExpression = "device = :device";
            exprValues[":device"] = new() { S = device };
        }

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (nextPageKey != null)
        {
            var keyItem = await _dynamoClient.GetItemAsync(TableName, new Dictionary<string, AttributeValue>
            {
                ["clip_id"] = new() { S = nextPageKey }
            });

            if (keyItem.IsItemSet)
            {
                exclusiveStartKey = new Dictionary<string, AttributeValue>
                {
                    ["pk"] = new() { S = "CLIP" },
                    ["timestamp"] = keyItem.Item["timestamp"],
                    ["clip_id"] = new() { S = nextPageKey }
                };
            }
        }

        // When using FilterExpression, loop until we have enough results
        if (filterExpression != null)
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            var lastKey = exclusiveStartKey;
            do
            {
                var resp = await _dynamoClient.QueryAsync(new QueryRequest
                {
                    TableName = TableName,
                    IndexName = "all-by-time",
                    KeyConditionExpression = "pk = :pk",
                    FilterExpression = filterExpression,
                    ExpressionAttributeValues = exprValues,
                    ScanIndexForward = false,
                    Limit = 100,
                    ExclusiveStartKey = lastKey
                });
                items.AddRange(resp.Items);
                lastKey = resp.LastEvaluatedKey;
            } while (items.Count < limit && lastKey != null && lastKey.Count > 0);

            var clips = items.Take(limit).Select(MapToClipSummary).ToList();
            var nextKey = lastKey?.GetValueOrDefault("clip_id")?.S;
            if (items.Count > limit)
                nextKey = items[limit - 1].GetValueOrDefault("clip_id")?.S;
            return new ClipListResponse(clips, nextKey, clips.Count);
        }

        var response = await _dynamoClient.QueryAsync(new QueryRequest
        {
            TableName = TableName,
            IndexName = "all-by-time",
            KeyConditionExpression = "pk = :pk",
            ExpressionAttributeValues = exprValues,
            ScanIndexForward = false,
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey
        });
        var result = response.Items.Select(MapToClipSummary).ToList();
        var resultKey = response.LastEvaluatedKey?.GetValueOrDefault("clip_id")?.S;

        return new ClipListResponse(result, resultKey, response.Count);
    }

    public async Task<ClipDetail?> GetClipByIdAsync(string clipId)
    {
        var response = await _dynamoClient.GetItemAsync(TableName, new Dictionary<string, AttributeValue>
        {
            ["clip_id"] = new() { S = clipId }
        });

        if (!response.IsItemSet) return null;
        return MapToClipDetail(response.Item);
    }

    public async Task<List<DetectionSummary>> GetDetectionsAsync(
        string? detectionType = null, string? dateFrom = null, string? dateTo = null, int limit = 50)
    {
        var filterExpressions = new List<string>();
        var expressionValues = new Dictionary<string, AttributeValue>();

        if (detectionType != null)
        {
            filterExpressions.Add("detection_type = :dt");
            expressionValues[":dt"] = new() { S = detectionType };
        }
        else
        {
            filterExpressions.Add("detection_type IN (:dt1, :dt2)");
            expressionValues[":dt1"] = new() { S = "my_dog" };
            expressionValues[":dt2"] = new() { S = "other_dog" };
        }

        var request = new ScanRequest
        {
            TableName = TableName,
            FilterExpression = string.Join(" AND ", filterExpressions),
            ExpressionAttributeValues = expressionValues,
            Limit = limit
        };

        var response = await _dynamoClient.ScanAsync(request);
        return response.Items.Select(MapToDetectionSummary).ToList();
    }

    private async Task<ClipListResponse> QueryByDateAsync(string date, string? device, int limit, string? nextPageKey)
    {
        string? filterExpression = null;
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":date"] = new() { S = date }
        };

        if (!string.IsNullOrEmpty(device))
        {
            filterExpression = "device = :device";
            exprValues[":device"] = new() { S = device };
        }

        if (filterExpression != null)
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            Dictionary<string, AttributeValue>? lastKey = null;
            do
            {
                var resp = await _dynamoClient.QueryAsync(new QueryRequest
                {
                    TableName = TableName,
                    IndexName = "by-date",
                    KeyConditionExpression = "#d = :date",
                    FilterExpression = filterExpression,
                    ExpressionAttributeNames = new Dictionary<string, string> { ["#d"] = "date" },
                    ExpressionAttributeValues = exprValues,
                    ScanIndexForward = false,
                    Limit = 100,
                    ExclusiveStartKey = lastKey
                });
                items.AddRange(resp.Items);
                lastKey = resp.LastEvaluatedKey;
            } while (items.Count < limit && lastKey != null && lastKey.Count > 0);

            var clips = items.Take(limit).Select(MapToClipSummary).ToList();
            return new ClipListResponse(clips, null, clips.Count);
        }

        var response = await _dynamoClient.QueryAsync(new QueryRequest
        {
            TableName = TableName,
            IndexName = "by-date",
            KeyConditionExpression = "#d = :date",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#d"] = "date" },
            ExpressionAttributeValues = exprValues,
            ScanIndexForward = false,
            Limit = limit
        });
        var clips2 = response.Items.Select(MapToClipSummary).ToList();
        var lastKey2 = response.LastEvaluatedKey?.GetValueOrDefault("clip_id")?.S;

        return new ClipListResponse(clips2, lastKey2, response.Count);
    }

    private ClipSummary MapToClipSummary(Dictionary<string, AttributeValue> item)
    {
        var keyframeKeys = item.GetValueOrDefault("keyframe_keys")?.SS ?? new List<string>();
        var thumbnailUrl = keyframeKeys.Count > 0 ? _s3UrlService.GetPresignedUrl(keyframeKeys[0], TimeSpan.FromHours(1)) : null;

        return new ClipSummary(
            ClipId: item.GetValueOrDefault("clip_id")?.S ?? "",
            S3Key: item.GetValueOrDefault("s3_key")?.S ?? "",
            Timestamp: long.TryParse(item.GetValueOrDefault("timestamp")?.N, out var ts) ? ts : 0,
            DurationSeconds: int.TryParse(item.GetValueOrDefault("duration_s")?.N, out var dur) ? dur : 0,
            Date: item.GetValueOrDefault("date")?.S ?? "",
            Device: item.GetValueOrDefault("device")?.S,
            KeyframeCount: int.TryParse(item.GetValueOrDefault("keyframe_count")?.N, out var kc) ? kc : 0,
            DetectionType: item.GetValueOrDefault("detection_type")?.S ?? "pending",
            DetectionCount: int.TryParse(item.GetValueOrDefault("detection_count")?.N, out var dc) ? dc : 0,
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "")
        {
            ThumbnailUrl = thumbnailUrl
        };
    }

    private ClipDetail MapToClipDetail(Dictionary<string, AttributeValue> item)
    {
        var s3Key = item.GetValueOrDefault("s3_key")?.S ?? "";
        var keyframeKeys = item.GetValueOrDefault("keyframe_keys")?.SS ?? new List<string>();

        var videoUrl = !string.IsNullOrEmpty(s3Key) ? _s3UrlService.GetPresignedUrl(s3Key, TimeSpan.FromHours(1)) : null;
        var keyframeUrls = keyframeKeys.Count > 0 ? _s3UrlService.GetPresignedUrls(keyframeKeys, TimeSpan.FromHours(1)) : null;

        return new ClipDetail(
            ClipId: item.GetValueOrDefault("clip_id")?.S ?? "",
            S3Key: s3Key,
            Timestamp: long.TryParse(item.GetValueOrDefault("timestamp")?.N, out var ts) ? ts : 0,
            DurationSeconds: int.TryParse(item.GetValueOrDefault("duration_s")?.N, out var dur) ? dur : 0,
            Date: item.GetValueOrDefault("date")?.S ?? "",
            Device: item.GetValueOrDefault("device")?.S,
            KeyframeCount: int.TryParse(item.GetValueOrDefault("keyframe_count")?.N, out var kc) ? kc : 0,
            KeyframeKeys: keyframeKeys,
            DetectionType: item.GetValueOrDefault("detection_type")?.S ?? "pending",
            DetectionCount: int.TryParse(item.GetValueOrDefault("detection_count")?.N, out var dc) ? dc : 0,
            Detections: item.GetValueOrDefault("detections")?.S,
            Labeled: item.GetValueOrDefault("labeled")?.BOOL ?? false,
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "",
            InferenceAt: item.GetValueOrDefault("inference_at")?.S)
        {
            VideoUrl = videoUrl,
            KeyframeUrls = keyframeUrls
        };
    }

    private static DetectionSummary MapToDetectionSummary(Dictionary<string, AttributeValue> item) => new(
        ClipId: item.GetValueOrDefault("clip_id")?.S ?? "",
        DetectionType: item.GetValueOrDefault("detection_type")?.S ?? "",
        DetectionCount: int.TryParse(item.GetValueOrDefault("detection_count")?.N, out var dc) ? dc : 0,
        Timestamp: long.TryParse(item.GetValueOrDefault("timestamp")?.N, out var ts) ? ts : 0,
        Date: item.GetValueOrDefault("date")?.S ?? "",
        Device: item.GetValueOrDefault("device")?.S,
        FirstKeyframeKey: item.GetValueOrDefault("keyframe_keys")?.SS?.FirstOrDefault());
}
