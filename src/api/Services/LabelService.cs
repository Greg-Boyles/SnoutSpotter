using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;

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
        var (confirmedCounts, breedCounts, withBoxes, withoutBoxes) = await CountConfirmedLabelsAsync();

        return new
        {
            total, dogs, noDogs, reviewed, unreviewed,
            myDog = confirmedCounts.GetValueOrDefault("my_dog"),
            otherDog = confirmedCounts.GetValueOrDefault("other_dog"),
            confirmedNoDog = confirmedCounts.GetValueOrDefault("no_dog"),
            myDogWithBoxes = withBoxes.GetValueOrDefault("my_dog"),
            myDogWithoutBoxes = withoutBoxes.GetValueOrDefault("my_dog"),
            otherDogWithBoxes = withBoxes.GetValueOrDefault("other_dog"),
            otherDogWithoutBoxes = withoutBoxes.GetValueOrDefault("other_dog"),
            breeds = breedCounts,
        };
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
                TableName = _config.LabelsTable,
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
        string? reviewed, string? label, string? confirmedLabel, string? breed, string? device, int limit, string? nextPageKey)
    {
        string? indexName = null;
        string? pkField = null;
        string? pkValue = null;
        var filterParts = new List<string>();
        var filterValues = new Dictionary<string, AttributeValue>();

        if (confirmedLabel != null)
        {
            // Direct query on confirmed_label GSI — no filter needed
            indexName = "by-confirmed-label";
            pkField = "confirmed_label";
            pkValue = confirmedLabel;
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

        if (!string.IsNullOrEmpty(breed))
        {
            filterParts.Add("breed = :breed");
            filterValues[":breed"] = new() { S = breed };
        }

        if (!string.IsNullOrEmpty(device))
        {
            filterParts.Add("device = :device");
            filterValues[":device"] = new() { S = device };
        }

        var filterExpression = filterParts.Count > 0 ? string.Join(" AND ", filterParts) : null;

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
            foreach (var kv in filterValues)
                exprValues[kv.Key] = kv.Value;

            // When using FilterExpression, DynamoDB Limit applies before filtering.
            // We must loop until we collect enough results or exhaust the index.
            if (filterExpression != null)
            {
                items = new List<Dictionary<string, AttributeValue>>();
                lastKey = exclusiveStartKey;
                do
                {
                    var response = await _dynamoDb.QueryAsync(new QueryRequest
                    {
                        TableName = _config.LabelsTable,
                        IndexName = indexName,
                        KeyConditionExpression = $"{pkField} = :val",
                        FilterExpression = filterExpression,
                        ExpressionAttributeValues = exprValues,
                        ScanIndexForward = false,
                        Limit = 100,
                        ExclusiveStartKey = lastKey
                    });
                    items.AddRange(response.Items);
                    lastKey = response.LastEvaluatedKey;
                } while (items.Count < limit && lastKey != null && lastKey.Count > 0);
            }
            else
            {
                var response = await _dynamoDb.QueryAsync(new QueryRequest
                {
                    TableName = _config.LabelsTable,
                    IndexName = indexName,
                    KeyConditionExpression = $"{pkField} = :val",
                    ExpressionAttributeValues = exprValues,
                    ScanIndexForward = false,
                    Limit = limit,
                    ExclusiveStartKey = exclusiveStartKey
                });
                items = response.Items;
                lastKey = response.LastEvaluatedKey;
            }
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

    public async Task UpdateLabelAsync(string keyframeKey, string confirmedLabel, string? breed = null)
    {
        // Map confirmed label to auto_label value for GSI consistency
        // my_dog → dog, other_dog → dog (it is a dog, just not ours), no_dog → no_dog
        var autoLabelValue = confirmedLabel is "my_dog" or "other_dog" ? "dog" : "no_dog";

        var updateExpr = "SET confirmed_label = :label, reviewed = :rev, reviewed_at = :at, " +
                         "original_auto_label = if_not_exists(original_auto_label, auto_label), " +
                         "auto_label = :auto";
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":label"] = new() { S = confirmedLabel },
            [":rev"] = new() { S = "true" },
            [":at"] = new() { S = DateTime.UtcNow.ToString("O") },
            [":auto"] = new() { S = autoLabelValue }
        };

        if (!string.IsNullOrEmpty(breed))
        {
            updateExpr += ", breed = :breed";
            exprValues[":breed"] = new() { S = breed };
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _config.LabelsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["keyframe_key"] = new() { S = keyframeKey }
            },
            UpdateExpression = updateExpr,
            ExpressionAttributeValues = exprValues
        });
    }

    public async Task BulkConfirmAsync(List<string> keyframeKeys, string confirmedLabel, string? breed = null)
    {
        // DynamoDB BatchWriteItem doesn't support UpdateItem, so use individual updates
        var tasks = keyframeKeys.Select(key => UpdateLabelAsync(key, confirmedLabel, breed));
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

    public async Task<Dictionary<string, string>> UploadTrainingImageAsync(Stream imageStream, string fileName, string confirmedLabel, string? breed = null)
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

        if (!string.IsNullOrEmpty(breed))
            item["breed"] = new() { S = breed };

        await _dynamoDb.PutItemAsync(_config.LabelsTable, item);

        var result = new Dictionary<string, string>
        {
            ["keyframe_key"] = s3Key,
            ["auto_label"] = autoLabelValue,
            ["confirmed_label"] = confirmedLabel,
            ["reviewed"] = "true",
            ["imageUrl"] = GetPresignedUrl(s3Key)
        };

        if (!string.IsNullOrEmpty(breed))
            result["breed"] = breed;

        return result;
    }

    public async Task<int> BackfillBreedAsync(string confirmedLabel, string breed)
    {
        var updated = 0;
        Dictionary<string, AttributeValue>? lastKey = null;

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _config.LabelsTable,
                IndexName = "by-review",
                KeyConditionExpression = "reviewed = :rev",
                FilterExpression = "confirmed_label = :cl AND attribute_not_exists(breed)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":rev"] = new() { S = "true" },
                    [":cl"] = new() { S = confirmedLabel }
                },
                ProjectionExpression = "keyframe_key",
                ExclusiveStartKey = lastKey
            });

            var tasks = response.Items.Select(item =>
                _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _config.LabelsTable,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["keyframe_key"] = item["keyframe_key"]
                    },
                    UpdateExpression = "SET breed = :breed",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":breed"] = new() { S = breed }
                    }
                }));

            await Task.WhenAll(tasks);
            updated += response.Items.Count;
            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);

        return updated;
    }

    public async Task<object> BackfillBoundingBoxesAsync(string? confirmedLabel, List<string>? keys = null)
    {
        List<string> allKeys;

        if (keys != null && keys.Count > 0)
        {
            allKeys = keys;
        }
        else
        {
            // Collect all reviewed dog labels with empty bounding boxes
            var labelsToProcess = confirmedLabel != null
                ? new[] { confirmedLabel }
                : new[] { "my_dog", "other_dog" };

            allKeys = new List<string>();

            foreach (var label in labelsToProcess)
            {
                Dictionary<string, AttributeValue>? lastKey = null;
                do
                {
                    var response = await _dynamoDb.QueryAsync(new QueryRequest
                    {
                        TableName = _config.LabelsTable,
                        IndexName = "by-confirmed-label",
                        KeyConditionExpression = "confirmed_label = :cl",
                        FilterExpression = "bounding_boxes = :empty OR attribute_not_exists(bounding_boxes)",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":cl"] = new() { S = label },
                            [":empty"] = new() { S = "[]" }
                        },
                        ProjectionExpression = "keyframe_key",
                        ExclusiveStartKey = lastKey
                    });

                    allKeys.AddRange(response.Items.Select(item => item["keyframe_key"].S));
                    lastKey = response.LastEvaluatedKey;
                } while (lastKey != null && lastKey.Count > 0);
            }
        }

        if (allKeys.Count == 0)
            return new { total = 0, batches = 0, message = "No labels found with missing bounding boxes" };

        if (string.IsNullOrEmpty(_config.BackfillQueueUrl))
            throw new InvalidOperationException("BACKFILL_QUEUE_URL is not configured");

        // Send each batch as an SQS message — Lambda processes them one at a time (MaxConcurrency=1)
        const int batchSize = 100;
        var batches = 0;
        using var sqsClient = new Amazon.SQS.AmazonSQSClient();

        for (var i = 0; i < allKeys.Count; i += batchSize)
        {
            var batch = allKeys.Skip(i).Take(batchSize).ToList();

            await sqsClient.SendMessageAsync(new Amazon.SQS.Model.SendMessageRequest
            {
                QueueUrl = _config.BackfillQueueUrl,
                MessageBody = JsonSerializer.Serialize(batch)
            });

            batches++;
        }

        return new { total = allKeys.Count, batches };
    }

    public async Task<Dictionary<string, string?>?> GetLabelAsync(string keyframeKey)
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _config.LabelsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["keyframe_key"] = new() { S = keyframeKey }
            }
        });

        if (!response.IsItemSet || response.Item.Count == 0)
            return null;

        var dict = new Dictionary<string, string?>();
        foreach (var (k, v) in response.Item)
        {
            if (v.S != null) dict[k] = v.S;
            else if (v.N != null) dict[k] = v.N;
        }

        dict["imageUrl"] = GetPresignedUrl(keyframeKey);
        return dict;
    }
}
