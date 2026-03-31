using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;

namespace SnoutSpotter.Api.Services;

public class LabelService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonS3 _s3;
    private readonly string _labelsTable;
    private readonly string _bucketName;
    private readonly string _autoLabelFunction;

    public LabelService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _labelsTable = configuration["LABELS_TABLE"] ?? "snout-spotter-labels";
        _bucketName = configuration["BUCKET_NAME"]
            ?? throw new InvalidOperationException("BUCKET_NAME not configured");
        _autoLabelFunction = configuration["AUTO_LABEL_FUNCTION"] ?? "snout-spotter-auto-label";
    }

    public async Task<object> TriggerAutoLabelAsync(string? date)
    {
        var client = new AmazonLambdaClient();
        var payload = JsonSerializer.Serialize(new { Date = date });

        var response = await client.InvokeAsync(new InvokeRequest
        {
            FunctionName = _autoLabelFunction,
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

        return new { total, dogs, noDogs, reviewed, unreviewed };
    }

    private async Task<int> CountAsync(string? indexName, string? pkValue)
    {
        if (indexName == null)
        {
            var scan = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _labelsTable,
                Select = Select.COUNT
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
            Select = Select.COUNT
        });
        return query.Count;
    }

    public async Task<(List<Dictionary<string, string>> items, string? nextPageKey)> GetLabelsAsync(
        string? reviewed, string? label, int limit, string? nextPageKey)
    {
        string? indexName = null;
        string? pkField = null;
        string? pkValue = null;

        if (reviewed != null)
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
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _labelsTable,
                IndexName = indexName,
                KeyConditionExpression = $"{pkField} = :val",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":val"] = new() { S = pkValue! }
                },
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
                TableName = _labelsTable,
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
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _labelsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["keyframe_key"] = new() { S = keyframeKey }
            },
            UpdateExpression = "SET confirmed_label = :label, reviewed = :rev, reviewed_at = :at",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":label"] = new() { S = confirmedLabel },
                [":rev"] = new() { S = "true" },
                [":at"] = new() { S = DateTime.UtcNow.ToString("O") }
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
            BucketName = _bucketName,
            Key = keyframeKey,
            Expires = DateTime.UtcNow.AddMinutes(15)
        });
    }
}
