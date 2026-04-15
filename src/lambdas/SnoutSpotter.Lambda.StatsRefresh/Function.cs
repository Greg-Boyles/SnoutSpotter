using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.StatsRefresh;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonIoT _iot;
    private readonly IAmazonIotData _iotData;
    private readonly string _clipsTable;
    private readonly string _labelsTable;
    private readonly string _statsTable;
    private readonly string _iotThingGroup;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _iot = new AmazonIoTClient(Amazon.RegionEndpoint.EUWest1);

        var endpoint = _iot.DescribeEndpointAsync(new DescribeEndpointRequest
        {
            EndpointType = "iot:Data-ATS"
        }).GetAwaiter().GetResult();

        _iotData = new AmazonIotDataClient(new AmazonIotDataConfig
        {
            ServiceURL = $"https://{endpoint.EndpointAddress}"
        });

        _clipsTable = Environment.GetEnvironmentVariable("TABLE_NAME") ?? "snout-spotter-clips";
        _labelsTable = Environment.GetEnvironmentVariable("LABELS_TABLE") ?? "snout-spotter-labels";
        _statsTable = Environment.GetEnvironmentVariable("STATS_TABLE") ?? "snout-spotter-stats";
        _iotThingGroup = Environment.GetEnvironmentVariable("IOT_THING_GROUP") ?? "snoutspotter-pis";
    }

    public async Task FunctionHandler(ILambdaContext context)
    {
        var refreshedAt = DateTime.UtcNow.ToString("O");
        context.Logger.LogInformation("Starting stats refresh");

        await Task.WhenAll(
            RefreshDashboardAsync(refreshedAt, context),
            RefreshActivityAsync(refreshedAt, context),
            RefreshLabelStatsAsync(refreshedAt, context));

        context.Logger.LogInformation("Stats refresh complete");
    }

    private async Task RefreshDashboardAsync(string refreshedAt, ILambdaContext context)
    {
        var today = DateTime.UtcNow.ToString("yyyy/MM/dd");

        var totalClipsTask = CountClipsAsync();
        var todayClipsTask = CountClipsForDateAsync(today);
        var myDogTask = CountDetectionsAsync("my_dog");
        var otherDogTask = CountDetectionsAsync("other_dog");
        var piTask = GetPiStatsAsync();

        await Task.WhenAll(totalClipsTask, todayClipsTask, myDogTask, otherDogTask, piTask);

        var (piOnlineCount, piTotalCount, lastUploadAt) = piTask.Result;
        var totalDetections = myDogTask.Result + otherDogTask.Result;

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _statsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new() { S = "dashboard" },
                ["total_clips"] = new() { N = totalClipsTask.Result.ToString() },
                ["clips_today"] = new() { N = todayClipsTask.Result.ToString() },
                ["total_detections"] = new() { N = totalDetections.ToString() },
                ["my_dog_detections"] = new() { N = myDogTask.Result.ToString() },
                ["last_upload_time"] = new() { S = lastUploadAt ?? "" },
                ["pi_online_count"] = new() { N = piOnlineCount.ToString() },
                ["pi_total_count"] = new() { N = piTotalCount.ToString() },
                ["refreshed_at"] = new() { S = refreshedAt }
            }
        });

        context.Logger.LogInformation($"Dashboard stats written: totalClips={totalClipsTask.Result}, today={todayClipsTask.Result}, detections={totalDetections}");
    }

    private async Task RefreshActivityAsync(string refreshedAt, ILambdaContext context)
    {
        const int days = 14;
        var activityTasks = Enumerable.Range(0, days)
            .Select(i => DateTime.UtcNow.AddDays(-(days - 1 - i)))
            .Select(async d => new
            {
                date = d.ToString("yyyy-MM-dd"),
                clips = await CountClipsForDateAsync(d.ToString("yyyy/MM/dd"))
            });

        var activity = await Task.WhenAll(activityTasks);

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _statsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new() { S = "activity" },
                ["data"] = new() { S = JsonSerializer.Serialize(activity) },
                ["refreshed_at"] = new() { S = refreshedAt }
            }
        });

        context.Logger.LogInformation("Activity stats written");
    }

    private async Task RefreshLabelStatsAsync(string refreshedAt, ILambdaContext context)
    {
        var totalTask = CountLabelsAsync(null, null);
        var unreviewedTask = CountLabelsAsync("by-review", "false");
        var reviewedTask = CountLabelsAsync("by-review", "true");
        var confirmedTask = CountConfirmedLabelsAsync();

        await Task.WhenAll(totalTask, unreviewedTask, reviewedTask, confirmedTask);

        var (confirmedCounts, breedCounts, withBoxes, withoutBoxes) = confirmedTask.Result;

        var stats = new
        {
            total = totalTask.Result,
            reviewed = reviewedTask.Result,
            unreviewed = unreviewedTask.Result,
            myDog = confirmedCounts.GetValueOrDefault("my_dog"),
            otherDog = confirmedCounts.GetValueOrDefault("other_dog"),
            confirmedNoDog = confirmedCounts.GetValueOrDefault("no_dog"),
            myDogWithBoxes = withBoxes.GetValueOrDefault("my_dog"),
            myDogWithoutBoxes = withoutBoxes.GetValueOrDefault("my_dog"),
            otherDogWithBoxes = withBoxes.GetValueOrDefault("other_dog"),
            otherDogWithoutBoxes = withoutBoxes.GetValueOrDefault("other_dog"),
            breeds = breedCounts
        };

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _statsTable,
            Item = new Dictionary<string, AttributeValue>
            {
                ["stat_id"] = new() { S = "label_stats" },
                ["data"] = new() { S = JsonSerializer.Serialize(stats) },
                ["refreshed_at"] = new() { S = refreshedAt }
            }
        });

        context.Logger.LogInformation("Label stats written");
    }

    // --- Clips ---

    private async Task<int> CountClipsAsync()
    {
        int total = 0;
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _clipsTable,
                IndexName = "all-by-time",
                KeyConditionExpression = "pk = :pk",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":pk"] = new() { S = "CLIP" }
                },
                Select = "COUNT",
                ExclusiveStartKey = lastKey
            });
            total += response.Count;
            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);
        return total;
    }

    private async Task<int> CountClipsForDateAsync(string date)
    {
        int total = 0;
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _clipsTable,
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

    private async Task<int> CountDetectionsAsync(string detectionType)
    {
        int total = 0;
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _clipsTable,
                IndexName = "by-detection",
                KeyConditionExpression = "detection_type = :dt",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":dt"] = new() { S = detectionType }
                },
                Select = "COUNT",
                ExclusiveStartKey = lastKey
            });
            total += response.Count;
            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);
        return total;
    }

    // --- IoT ---

    private async Task<(int onlineCount, int totalCount, string? lastUploadAt)> GetPiStatsAsync()
    {
        List<string> things;
        try
        {
            var response = await _iot.ListThingsInThingGroupAsync(new ListThingsInThingGroupRequest
            {
                ThingGroupName = _iotThingGroup
            });
            things = response.Things;
        }
        catch
        {
            return (0, 0, null);
        }

        var shadowTasks = things.Select(GetShadowAsync);
        var shadows = await Task.WhenAll(shadowTasks);

        var onlineCount = 0;
        string? lastUpload = null;

        foreach (var shadow in shadows)
        {
            if (shadow.HasValue && shadow.Value.LastHeartbeat != null &&
                DateTime.TryParse(shadow.Value.LastHeartbeat, out var hb) &&
                (DateTime.UtcNow - hb).TotalMinutes < 5)
                onlineCount++;

            if (shadow.HasValue && shadow.Value.LastUploadAt != null &&
                (lastUpload == null || string.Compare(shadow.Value.LastUploadAt, lastUpload, StringComparison.Ordinal) > 0))
                lastUpload = shadow.Value.LastUploadAt;
        }

        return (onlineCount, things.Count, lastUpload);
    }

    private async Task<(string? LastHeartbeat, string? LastUploadAt)?> GetShadowAsync(string thingName)
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

            var lastHeartbeat = reported.TryGetProperty("lastHeartbeat", out var lh) ? lh.GetString() : null;
            var lastUploadAt = reported.TryGetProperty("lastUploadAt", out var lu) ? lu.GetString() : null;
            return (lastHeartbeat, lastUploadAt);
        }
        catch
        {
            return null;
        }
    }

    // --- Labels ---

    private async Task<int> CountLabelsAsync(string? indexName, string? pkValue)
    {
        if (indexName == null)
        {
            var scan = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _labelsTable,
                Select = "COUNT"
            });
            return scan.Count;
        }

        var pkField = indexName == "by-label" ? "auto_label" : "reviewed";
        var query = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _labelsTable,
            IndexName = indexName,
            KeyConditionExpression = $"{pkField} = :val",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":val"] = new() { S = pkValue! }
            },
            Select = "COUNT"
        });
        return query.Count;
    }

    private async Task<(Dictionary<string, int> labels, Dictionary<string, int> breeds, Dictionary<string, int> withBoxes, Dictionary<string, int> withoutBoxes)> CountConfirmedLabelsAsync()
    {
        var counts = new Dictionary<string, int> { ["my_dog"] = 0, ["other_dog"] = 0, ["no_dog"] = 0 };
        var breedCounts = new Dictionary<string, int>();
        var withBoxes = new Dictionary<string, int> { ["my_dog"] = 0, ["other_dog"] = 0 };
        var withoutBoxes = new Dictionary<string, int> { ["my_dog"] = 0, ["other_dog"] = 0 };
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _labelsTable,
                IndexName = "by-review",
                KeyConditionExpression = "reviewed = :rev",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":rev"] = new() { S = "true" }
                },
                ProjectionExpression = "confirmed_label, breed, bounding_boxes",
                ExclusiveStartKey = lastKey
            });

            foreach (var item in response.Items)
            {
                var label = item.GetValueOrDefault("confirmed_label")?.S ?? "";
                if (counts.ContainsKey(label))
                    counts[label]++;

                if (label is "my_dog" or "other_dog")
                {
                    var boxes = item.GetValueOrDefault("bounding_boxes")?.S ?? "[]";
                    if (boxes != "[]" && !string.IsNullOrEmpty(boxes))
                        withBoxes[label]++;
                    else
                        withoutBoxes[label]++;
                }

                var breed = item.GetValueOrDefault("breed")?.S;
                if (!string.IsNullOrEmpty(breed))
                {
                    breedCounts.TryGetValue(breed, out var count);
                    breedCounts[breed] = count + 1;
                }
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);

        return (counts, breedCounts, withBoxes, withoutBoxes);
    }
}
