using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace SnoutSpotter.Api.Services;

public class LabelService : ILabelService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonS3 _s3;
    private readonly AppConfig _config;

    public LabelService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _config = config.Value;
    }

    public async Task<object> TriggerAutoLabelAsync(string? date)
    {
        var client = new AmazonLambdaClient();
        var payload = JsonSerializer.Serialize(new { Date = date });

        var response = await client.InvokeAsync(new InvokeRequest
        {
            FunctionName = _config.AutoLabelFunction,
            InvocationType = InvocationType.Event, // Async — don't wait
            Payload = payload
        });

        return new { message = "Auto-label started", statusCode = (int)response.StatusCode };
    }

    public async Task<object> GetStatsAsync()
    {
        var total = await CountAsync(null, null);
        var dogs = await CountAsync("by-label", "dog");
        var noDogs = await CountAsync("by-label", "no_dog");
        var unreviewed = await CountAsync("by-review", "false");
        var reviewed = await CountAsync("by-review", "true");
        var confirmedCounts = await CountConfirmedLabelsAsync();

        return new
        {
            total, dogs, noDogs, reviewed, unreviewed,
            myDog = confirmedCounts.GetValueOrDefault("my_dog"),
            otherDog = confirmedCounts.GetValueOrDefault("other_dog"),
            confirmedNoDog = confirmedCounts.GetValueOrDefault("no_dog"),
        };
    }

    private async Task<Dictionary<string, int>> CountConfirmedLabelsAsync()
    {
        var counts = new Dictionary<string, int> { ["my_dog"] = 0, ["other_dog"] = 0, ["no_dog"] = 0 };
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _config.LabelsTable,
                IndexName = "by-review",
                KeyConditionExpression = "reviewed = :rev",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":rev"] = new() { S = "true" }
                },
                ProjectionExpression = "confirmed_label",
                ExclusiveStartKey = lastKey
            });

            foreach (var item in response.Items)
            {
                var label = item.GetValueOrDefault("confirmed_label")?.S ?? "";
                if (counts.ContainsKey(label))
                    counts[label]++;
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);

        return counts;
    }

    private async Task<int> CountAsync(string? indexName, string? pkValue)
    {
        if (indexName == null)
        {
            var scan = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _config.LabelsTable,
                Select = Select.COUNT
            });
            return scan.Count;
        }

        var pkField = indexName == "by-label" ? "auto_label" : "reviewed";
        var query = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _config.LabelsTable,
            IndexName = indexName,
            KeyConditionExpression = $"{pkField} = :val",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":val"] = new() { S = pkValue! }
            },
            Select = Select.COUNT
        });
        return query.Count;
    }

    public async Task<(List<Dictionary<string, string>> items, string? nextPageKey)> GetLabelsAsync(
        string? reviewed, string? label, string? confirmedLabel, int limit, string? nextPageKey)
    {
        string? indexName = null;
        string? pkField = null;
        string? pkValue = null;
        string? filterExpression = null;
        Dictionary<string, AttributeValue>? filterValues = null;

        if (confirmedLabel != null)
        {
            // Query reviewed=true items and filter by confirmed_label
            indexName = "by-review";
            pkField = "reviewed";
            pkValue = "true";
            filterExpression = "confirmed_label = :cl";
            filterValues = new Dictionary<string, AttributeValue>
            {
                [":cl"] = new() { S = confirmedLabel }
            };
        }
        else if (reviewed != null)
        {
            indexName = "by-review";
            pkField = "reviewed";
            pkValue = reviewed;
        }
        else if (label != null)
        {
            indexName = "by-label";
            pkField = "auto_label";
            pkValue = label;
        }

        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (!string.IsNullOrEmpty(nextPageKey))
        {
            exclusiveStartKey = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                Convert.FromBase64String(nextPageKey))
                ?.ToDictionary(kv => kv.Key, kv => new AttributeValue { S = kv.Value.GetString() });
        }

        List<Dictionary<string, AttributeValue>> items;
        Dictionary<string, AttributeValue>? lastKey;

        if (indexName != null && pkField != null)
        {
            var exprValues = new Dictionary<string, AttributeValue>
            {
                [":val"] = new() { S = pkValue! }
            };
            if (filterValues != null)
                foreach (var kv in filterValues)
                    exprValues[kv.Key] = kv.Value;

            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _config.LabelsTable,
                IndexName = indexName,
                KeyConditionExpression = $"{pkField} = :val",
                FilterExpression = filterExpression,
                ExpressionAttributeValues = exprValues,
                ScanIndexForward = false,
                Limit = limit,
                ExclusiveStartKey = exclusiveStartKey
            });
            items = response.Items;
            lastKey = response.LastEvaluatedKey;
        }
        else
        {
            var response = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _config.LabelsTable,
                Limit = limit,
                ExclusiveStartKey = exclusiveStartKey
            });
            items = response.Items;
            lastKey = response.LastEvaluatedKey;
        }

        var result = items.Select(item =>
        {
            var dict = new Dictionary<string, string>();
            foreach (var (k, v) in item)
            {
                if (v.S != null) dict[k] = v.S;
                else if (v.N != null) dict[k] = v.N;
            }
            return dict;
        }).ToList();

        string? nextKey = null;
        if (lastKey != null && lastKey.Count > 0)
        {
            var keyDict = lastKey.ToDictionary(kv => kv.Key, kv => kv.Value.S ?? kv.Value.N ?? "");
            nextKey = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(keyDict)));
        }

        return (result, nextKey);
    }

    public async Task UpdateLabelAsync(string keyframeKey, string confirmedLabel)
    {
        // Map confirmed label to auto_label value for GSI consistency
        // my_dog → dog, other_dog → dog (it is a dog, just not ours), no_dog → no_dog
        var autoLabelValue = confirmedLabel is "my_dog" or "other_dog" ? "dog" : "no_dog";

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _config.LabelsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["keyframe_key"] = new() { S = keyframeKey }
            },
            UpdateExpression = "SET confirmed_label = :label, reviewed = :rev, reviewed_at = :at, " +
                               "original_auto_label = if_not_exists(original_auto_label, auto_label), " +
                               "auto_label = :auto",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":label"] = new() { S = confirmedLabel },
                [":rev"] = new() { S = "true" },
                [":at"] = new() { S = DateTime.UtcNow.ToString("O") },
                [":auto"] = new() { S = autoLabelValue }
            }
        });
    }

    public async Task BulkConfirmAsync(List<string> keyframeKeys, string confirmedLabel)
    {
        // DynamoDB BatchWriteItem doesn't support UpdateItem, so use individual updates
        var tasks = keyframeKeys.Select(key => UpdateLabelAsync(key, confirmedLabel));
        await Task.WhenAll(tasks);
    }

    public string GetPresignedUrl(string keyframeKey)
    {
        return _s3.GetPreSignedURL(new Amazon.S3.Model.GetPreSignedUrlRequest
        {
            BucketName = _config.BucketName,
            Key = keyframeKey,
            Expires = DateTime.UtcNow.AddMinutes(15)
        });
    }

    public async Task<Dictionary<string, string>> UploadTrainingImageAsync(Stream imageStream, string fileName, string confirmedLabel)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png"))
            ext = ".jpg";

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH-mm-ss");
        var id = Guid.NewGuid().ToString("N")[..8];
        var s3Key = $"training-uploads/{timestamp}_{id}{ext}";

        await _s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = _config.BucketName,
            Key = s3Key,
            InputStream = imageStream,
            ContentType = ext == ".png" ? "image/png" : "image/jpeg"
        });

        var autoLabelValue = confirmedLabel is "my_dog" or "other_dog" ? "dog" : "no_dog";
        var now = DateTime.UtcNow.ToString("O");
        var item = new Dictionary<string, AttributeValue>
        {
            ["keyframe_key"] = new() { S = s3Key },
            ["clip_id"] = new() { S = "uploaded" },
            ["auto_label"] = new() { S = autoLabelValue },
            ["confirmed_label"] = new() { S = confirmedLabel },
            ["confidence"] = new() { N = "1" },
            ["bounding_boxes"] = new() { S = "[]" },
            ["reviewed"] = new() { S = "true" },
            ["labelled_at"] = new() { S = now },
            ["reviewed_at"] = new() { S = now },
        };

        await _dynamoDb.PutItemAsync(_config.LabelsTable, item);

        return new Dictionary<string, string>
        {
            ["keyframe_key"] = s3Key,
            ["auto_label"] = autoLabelValue,
            ["confirmed_label"] = confirmedLabel,
            ["reviewed"] = "true",
            ["imageUrl"] = GetPresignedUrl(s3Key)
        };
    }
}
