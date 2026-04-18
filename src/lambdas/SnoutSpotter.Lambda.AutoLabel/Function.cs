using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using SnoutSpotter.Contracts;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.AutoLabel;

public class AutoLabelRequest
{
    public string? Date { get; set; } // Optional: "YYYY/MM/DD" to scope to a specific day
    public List<string>? ReprocessKeys { get; set; } // Optional: reprocess specific keys to backfill bounding boxes
}

public class Function
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _bucketName;
    private readonly string _tableName;
    private readonly SettingsReader _settings;
    private InferenceSession? _session;
    private string? _loadedModelKey;
    private float _dogConfidenceThreshold = 0.25f;

    public Function()
    {
        _s3 = new AmazonS3Client();
        _dynamoDb = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")
            ?? throw new InvalidOperationException("BUCKET_NAME not set");
        _tableName = Environment.GetEnvironmentVariable("LABELS_TABLE")
            ?? throw new InvalidOperationException("LABELS_TABLE not set");
        _settings = new SettingsReader(_dynamoDb);
    }

    // Single entry point handles both direct invocations (AutoLabelRequest) and SQS trigger events
    public async Task<object> FunctionHandler(System.Text.Json.JsonElement request, ILambdaContext context)
    {
        var modelKey = await _settings.GetStringAsync(ServerSettings.AutoLabelModelKey);
        await EnsureModelLoaded(modelKey, context);
        _dogConfidenceThreshold = await _settings.GetFloatAsync(ServerSettings.AutoLabelConfidenceThreshold);

        // SQS event: { "Records": [ { "body": "{\"KeyframeKeys\":[...]}" }, ... ] }
        if (request.TryGetProperty("Records", out var records))
        {
            var allKeys = new List<string>();
            foreach (var record in records.EnumerateArray())
            {
                var body = record.GetProperty("body").GetString() ?? "{}";
                var msg = JsonSerializer.Deserialize<BackfillMessage>(body);
                if (msg?.KeyframeKeys != null)
                    allKeys.AddRange(msg.KeyframeKeys);
            }
            context.Logger.LogInformation($"SQS trigger: {allKeys.Count} keys to reprocess");
            return await ReprocessHandler(allKeys, context);
        }

        // Direct invocation: AutoLabelRequest
        var autoLabelRequest = System.Text.Json.JsonSerializer.Deserialize<AutoLabelRequest>(request.GetRawText())
            ?? new AutoLabelRequest();

        if (autoLabelRequest.ReprocessKeys != null && autoLabelRequest.ReprocessKeys.Count > 0)
            return await ReprocessHandler(autoLabelRequest.ReprocessKeys, context);

        var prefix = "keyframes/";
        if (!string.IsNullOrEmpty(autoLabelRequest.Date))
            prefix = $"keyframes/{autoLabelRequest.Date}/";

        // List all keyframes
        var keyframes = new List<string>();
        string? continuationToken = null;
        do
        {
            var listResponse = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucketName,
                Prefix = prefix,
                ContinuationToken = continuationToken
            });
            keyframes.AddRange(listResponse.S3Objects
                .Where(o => o.Key.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
                .Select(o => o.Key));
            continuationToken = listResponse.IsTruncated ? listResponse.NextContinuationToken : null;
        } while (continuationToken != null);

        context.Logger.LogInformation($"Found {keyframes.Count} keyframes under {prefix}");

        var processed = 0;
        var skipped = 0;
        var dogsFound = 0;

        foreach (var keyframeKey in keyframes)
        {
            // Check remaining time — stop if less than 30 seconds left
            if (context.RemainingTime.TotalSeconds < 30)
            {
                context.Logger.LogWarning($"Running low on time, stopping after {processed} keyframes");
                break;
            }

            // Skip if already labelled
            var existing = await _dynamoDb.GetItemAsync(_tableName,
                new Dictionary<string, AttributeValue>
                {
                    ["keyframe_key"] = new() { S = keyframeKey }
                });
            if (existing.IsItemSet)
            {
                skipped++;
                continue;
            }

            try
            {
                var (label, confidence, boxes) = await DetectDogs(keyframeKey);

                // Extract clip_id and device from keyframe key
                var clipId = ExtractClipId(keyframeKey);
                var device = ExtractDevice(keyframeKey);

                var householdId = ExtractHouseholdId(keyframeKey);

                var item = new Dictionary<string, AttributeValue>
                {
                    ["keyframe_key"] = new() { S = keyframeKey },
                    ["clip_id"] = new() { S = clipId },
                    ["auto_label"] = new() { S = label },
                    ["confidence"] = new() { N = confidence.ToString("F4") },
                    ["bounding_boxes"] = new() { S = JsonSerializer.Serialize(boxes) },
                    ["reviewed"] = new() { S = "false" },
                    ["labelled_at"] = new() { S = DateTime.UtcNow.ToString("O") },
                };

                if (!string.IsNullOrEmpty(householdId))
                    item["household_id"] = new() { S = householdId };
                if (!string.IsNullOrEmpty(device))
                    item["device"] = new() { S = device };

                await _dynamoDb.PutItemAsync(_tableName, item);
                processed++;
                if (label == "dog") dogsFound++;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to process {keyframeKey}: {ex.Message}");
            }
        }

        context.Logger.LogInformation($"Done: {processed} processed, {skipped} skipped, {dogsFound} dogs found");
        return new { processed, skipped, dogsFound, total = keyframes.Count };
    }

    private async Task<object> ReprocessHandler(List<string> keys, ILambdaContext context)
    {
        context.Logger.LogInformation($"Reprocess mode: {keys.Count} keys to reprocess for bounding boxes");

        var reprocessed = 0;
        var failed = 0;

        foreach (var keyframeKey in keys)
        {
            if (context.RemainingTime.TotalSeconds < 30)
            {
                context.Logger.LogWarning($"Running low on time, stopping after {reprocessed} reprocessed");
                break;
            }

            try
            {
                var (_, confidence, boxes) = await DetectDogs(keyframeKey);

                await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _tableName,
                    Key = new Dictionary<string, AttributeValue>
                    {
                        ["keyframe_key"] = new() { S = keyframeKey }
                    },
                    UpdateExpression = "SET bounding_boxes = :boxes, confidence = :conf",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        [":boxes"] = new() { S = JsonSerializer.Serialize(boxes) },
                        [":conf"] = new() { N = confidence.ToString("F4") }
                    }
                });

                reprocessed++;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to reprocess {keyframeKey}: {ex.Message}");
                failed++;
            }
        }

        context.Logger.LogInformation($"Reprocess done: {reprocessed} updated, {failed} failed");
        return new { reprocessed, failed, total = keys.Count };
    }

    private async Task EnsureModelLoaded(string modelKey, ILambdaContext context)
    {
        if (_session != null && _loadedModelKey == modelKey) return;

        if (_session != null && _loadedModelKey != modelKey)
        {
            context.Logger.LogInformation($"Model key changed from {_loadedModelKey} to {modelKey} — reloading");
            _session.Dispose();
            _session = null;
        }

        var localPath = $"/tmp/{Path.GetFileName(modelKey)}";
        if (!File.Exists(localPath))
        {
            context.Logger.LogInformation($"Downloading model from s3://{_bucketName}/{modelKey}");
            var response = await _s3.GetObjectAsync(_bucketName, modelKey);
            await using var fileStream = File.Create(localPath);
            await response.ResponseStream.CopyToAsync(fileStream);
        }

        _session = new InferenceSession(localPath);
        _loadedModelKey = modelKey;
        context.Logger.LogInformation($"Model loaded: {modelKey}");
    }

    private async Task<(string label, float confidence, List<float[]> boxes)> DetectDogs(string keyframeKey)
    {
        var response = await _s3.GetObjectAsync(_bucketName, keyframeKey);
        using var image = await Image.LoadAsync<Rgb24>(response.ResponseStream);

        const int targetSize = 640;
        using var resized = image.Clone(ctx => ctx.Resize(targetSize, targetSize));

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetSize, targetSize });
        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < targetSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < targetSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }
        });

        var inputName = _session!.InputNames[0];
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results.First().AsTensor<float>();

        var scaleX = (float)image.Width / targetSize;
        var scaleY = (float)image.Height / targetSize;

        var boxes = new List<float[]>();
        float maxConfidence = 0;

        // YOLOv8 output: [1, 84, 8400] — 4 bbox + 80 classes
        var numDetections = output.Dimensions[2];
        for (var i = 0; i < numDetections; i++)
        {
            var dogConfidence = output[0, 4 + 16, i]; // Class 16 = dog in COCO
            if (dogConfidence < _dogConfidenceThreshold) continue;

            var cx = output[0, 0, i] * scaleX;
            var cy = output[0, 1, i] * scaleY;
            var w = output[0, 2, i] * scaleX;
            var h = output[0, 3, i] * scaleY;

            boxes.Add(new[]
            {
                Math.Max(0, cx - w / 2),
                Math.Max(0, cy - h / 2),
                w, h,
                dogConfidence
            });

            if (dogConfidence > maxConfidence)
                maxConfidence = dogConfidence;
        }

        var label = boxes.Count > 0 ? "dog" : "no_dog";
        return (label, maxConfidence, boxes);
    }

    private static string ExtractClipId(string keyframeKey)
    {
        // keyframes/{device}/YYYY/MM/DD/filename_0.jpg or keyframes/YYYY/MM/DD/filename_0.jpg
        var filename = Path.GetFileNameWithoutExtension(keyframeKey);
        var lastUnderscore = filename.LastIndexOf('_');
        return lastUnderscore > 0 ? filename[..lastUnderscore] : filename;
    }

    private static string ExtractDevice(string keyframeKey)
    {
        // Household-prefixed: {hh}/keyframes/{device}/YYYY/MM/DD/filename.jpg
        // New format: keyframes/{device}/YYYY/MM/DD/filename.jpg (5+ parts)
        // Old format: keyframes/YYYY/MM/DD/filename.jpg (4 parts)
        var parts = keyframeKey.Split('/');
        if (parts.Length >= 2 && parts[0] != "keyframes")
            parts = parts[1..]; // strip household prefix
        return parts.Length >= 5 ? parts[1] : "";
    }

    private static string? ExtractHouseholdId(string keyframeKey)
    {
        // {household_id}/keyframes/... → extract first segment
        // keyframes/... → no household
        var firstSlash = keyframeKey.IndexOf('/');
        if (firstSlash < 0) return null;
        var firstSegment = keyframeKey[..firstSlash];
        return firstSegment == "keyframes" ? null : firstSegment;
    }
}
