using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class ClipService : IClipService
{
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly IS3UrlService _s3UrlService;
    private readonly IAmazonS3 _s3;
    private readonly AppConfig _config;
    private const string TableName = "snout-spotter-clips";

    public ClipService(IAmazonDynamoDB dynamoClient, IS3UrlService s3UrlService, IAmazonS3 s3, IOptions<AppConfig> config)
    {
        _dynamoClient = dynamoClient;
        _s3UrlService = s3UrlService;
        _s3 = s3;
        _config = config.Value;
    }

    public async Task<ClipListResponse> GetClipsAsync(string? date = null, string? device = null, string? detectionType = null, int limit = 20, string? nextPageKey = null)
    {
        if (date != null)
        {
            return await QueryByDateAsync(date, device, detectionType, limit, nextPageKey);
        }

        if (detectionType != null)
        {
            return await QueryByDetectionTypeAsync(detectionType, device, limit, nextPageKey);
        }

        if (!string.IsNullOrEmpty(device))
        {
            return await QueryByDeviceAsync(device, limit, nextPageKey);
        }

        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":pk"] = new() { S = "CLIP" }
        };

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

    private async Task<ClipListResponse> QueryByDeviceAsync(string device, int limit, string? nextPageKey)
    {
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":device"] = new() { S = device }
        };

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
                    ["device"] = new() { S = device },
                    ["timestamp"] = keyItem.Item["timestamp"],
                    ["clip_id"] = new() { S = nextPageKey }
                };
            }
        }

        var response = await _dynamoClient.QueryAsync(new QueryRequest
        {
            TableName = TableName,
            IndexName = "by-device",
            KeyConditionExpression = "device = :device",
            ExpressionAttributeValues = exprValues,
            ScanIndexForward = false,
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey
        });

        var clips = response.Items.Select(MapToClipSummary).ToList();
        var nextKey = response.LastEvaluatedKey?.GetValueOrDefault("clip_id")?.S;
        return new ClipListResponse(clips, nextKey, response.Count);
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

        // Scan with a filter: Limit controls items examined, not items returned.
        // Paginate until we have enough matching items or the table is exhausted.
        var items = new List<Dictionary<string, AttributeValue>>();
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var page = await _dynamoClient.ScanAsync(new ScanRequest
            {
                TableName = TableName,
                FilterExpression = string.Join(" AND ", filterExpressions),
                ExpressionAttributeValues = expressionValues,
                ExclusiveStartKey = lastKey?.Count > 0 ? lastKey : null
            });
            items.AddRange(page.Items);
            lastKey = page.LastEvaluatedKey;
        } while (items.Count < limit && lastKey != null && lastKey.Count > 0);

        return items
            .OrderByDescending(i => long.TryParse(i.GetValueOrDefault("timestamp")?.N, out var t) ? t : 0)
            .Take(limit)
            .Select(MapToDetectionSummary)
            .ToList();
    }

    public async Task<int> GetClipCountForDateAsync(string date)
    {
        int total = 0;
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await _dynamoClient.QueryAsync(new QueryRequest
            {
                TableName = TableName,
                IndexName = "by-date",
                KeyConditionExpression = "#d = :date",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#d"] = "date" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":date"] = new() { S = date }
                },
                Select = "COUNT",
                ExclusiveStartKey = lastKey
            });
            total += response.Count;
            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);

        return total;
    }

    private async Task<ClipListResponse> QueryByDetectionTypeAsync(string detectionType, string? device, int limit, string? nextPageKey)
    {
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":dt"] = new() { S = detectionType }
        };

        string? filterExpression = null;
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
                    ["detection_type"] = new() { S = detectionType },
                    ["timestamp"] = keyItem.Item["timestamp"],
                    ["clip_id"] = new() { S = nextPageKey }
                };
            }
        }

        if (filterExpression != null)
        {
            var items = new List<Dictionary<string, AttributeValue>>();
            var lastKey = exclusiveStartKey;
            do
            {
                var resp = await _dynamoClient.QueryAsync(new QueryRequest
                {
                    TableName = TableName,
                    IndexName = "by-detection",
                    KeyConditionExpression = "detection_type = :dt",
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
            var nextKey = items.Count > limit ? items[limit - 1].GetValueOrDefault("clip_id")?.S
                : lastKey?.GetValueOrDefault("clip_id")?.S;
            return new ClipListResponse(clips, nextKey, clips.Count);
        }

        var response = await _dynamoClient.QueryAsync(new QueryRequest
        {
            TableName = TableName,
            IndexName = "by-detection",
            KeyConditionExpression = "detection_type = :dt",
            ExpressionAttributeValues = exprValues,
            ScanIndexForward = false,
            Limit = limit,
            ExclusiveStartKey = exclusiveStartKey
        });
        var result = response.Items.Select(MapToClipSummary).ToList();
        var resultKey = response.LastEvaluatedKey?.GetValueOrDefault("clip_id")?.S;
        return new ClipListResponse(result, resultKey, response.Count);
    }

    private async Task<ClipListResponse> QueryByDateAsync(string date, string? device, string? detectionType, int limit, string? nextPageKey)
    {
        var filterParts = new List<string>();
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":date"] = new() { S = date }
        };

        if (!string.IsNullOrEmpty(device))
        {
            filterParts.Add("device = :device");
            exprValues[":device"] = new() { S = device };
        }

        if (!string.IsNullOrEmpty(detectionType))
        {
            filterParts.Add("detection_type = :dtt");
            exprValues[":dtt"] = new() { S = detectionType };
        }

        string? filterExpression = filterParts.Count > 0 ? string.Join(" AND ", filterParts) : null;

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

    public async Task DeleteClipAsync(string clipId)
    {
        // 1. Fetch clip to get s3_key and keyframe_keys
        var result = await _dynamoClient.GetItemAsync(TableName,
            new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } });
        if (!result.IsItemSet) return;

        var s3Key = result.Item.GetValueOrDefault("s3_key")?.S;
        var keyframeKeys = result.Item.GetValueOrDefault("keyframe_keys")?.SS ?? [];

        // 2. Batch-delete S3 objects (video + all keyframes) in one request
        var objectsToDelete = new List<KeyVersion>();
        if (!string.IsNullOrEmpty(s3Key))
            objectsToDelete.Add(new KeyVersion { Key = s3Key });
        objectsToDelete.AddRange(keyframeKeys.Select(k => new KeyVersion { Key = k }));

        if (objectsToDelete.Count > 0)
        {
            try
            {
                await _s3.DeleteObjectsAsync(new DeleteObjectsRequest
                {
                    BucketName = _config.BucketName,
                    Objects = objectsToDelete
                });
            }
            catch { /* best effort */ }
        }

        // 3. Batch-delete label records — keyframe_key is the labels table primary key,
        //    so no scan needed; use the clip's keyframe_keys directly
        if (keyframeKeys.Count > 0)
        {
            foreach (var batch in keyframeKeys.Chunk(25))
            {
                var deleteRequests = batch.Select(kk => new WriteRequest
                {
                    DeleteRequest = new DeleteRequest
                    {
                        Key = new Dictionary<string, AttributeValue> { ["keyframe_key"] = new() { S = kk } }
                    }
                }).ToList();

                try
                {
                    await _dynamoClient.BatchWriteItemAsync(new BatchWriteItemRequest
                    {
                        RequestItems = new Dictionary<string, List<WriteRequest>>
                        {
                            [_config.LabelsTable] = deleteRequests
                        }
                    });
                }
                catch { /* best effort */ }
            }
        }

        // 4. Delete the clip record itself
        await _dynamoClient.DeleteItemAsync(TableName,
            new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } });
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
            KeyframeUrls = keyframeUrls,
            KeyframeDetections = ParseKeyframeDetections(item)
        };
    }

    private static List<KeyframeDetectionDto>? ParseKeyframeDetections(Dictionary<string, AttributeValue> item)
    {
        if (!item.TryGetValue("keyframe_detections", out var attr) || attr.L == null || attr.L.Count == 0)
            return null;

        return attr.L.Select(kf =>
        {
            var m = kf.M;
            var detections = m.GetValueOrDefault("detections")?.L?.Select(d =>
            {
                var dm = d.M;
                var bb = dm.GetValueOrDefault("boundingBox")?.M;
                return new DetectionBoxDto(
                    Label: dm.GetValueOrDefault("label")?.S ?? "",
                    Confidence: float.TryParse(dm.GetValueOrDefault("confidence")?.N, out var c) ? c : 0,
                    BoundingBox: new BoundingBoxDto(
                        X: float.TryParse(bb?.GetValueOrDefault("x")?.N, out var x) ? x : 0,
                        Y: float.TryParse(bb?.GetValueOrDefault("y")?.N, out var y) ? y : 0,
                        Width: float.TryParse(bb?.GetValueOrDefault("width")?.N, out var w) ? w : 0,
                        Height: float.TryParse(bb?.GetValueOrDefault("height")?.N, out var h) ? h : 0));
            }).ToList() ?? [];

            return new KeyframeDetectionDto(
                KeyframeKey: m.GetValueOrDefault("keyframeKey")?.S ?? "",
                Label: m.GetValueOrDefault("label")?.S ?? "",
                Detections: detections);
        }).ToList();
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
