using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.S3;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.RunInference;

public class Function
{
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly string _bucketName;
    private readonly string _tableName;
    private readonly string _classifierModelKey;

    private InferenceSession? _classifierSession;

    public Function()
    {
        _s3Client = new AmazonS3Client();
        _dynamoClient = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")!;
        _tableName = Environment.GetEnvironmentVariable("TABLE_NAME")!;
        _classifierModelKey = Environment.GetEnvironmentVariable("CLASSIFIER_MODEL_KEY")!;
    }

    public async Task FunctionHandler(JsonElement input, ILambdaContext context)
    {
        // Normalise input: EventBridge event or direct invocation
        var (clipId, isEventBridge) = ParseInput(input);
        context.Logger.LogInformation($"Running inference for clip {clipId} (source={( isEventBridge ? "eventbridge" : "direct" )})");

        // Get clip record from DynamoDB (with retry for EventBridge path)
        var clipItem = await GetClipRecord(clipId, isEventBridge, context);
        if (clipItem == null)
        {
            context.Logger.LogWarning($"Clip {clipId} not found — skipping");
            return;
        }

        // Get keyframe keys from the clip record
        var keyframeKeys = clipItem.TryGetValue("keyframe_keys", out var kk) ? kk.SS : [];
        if (keyframeKeys.Count == 0)
        {
            context.Logger.LogWarning($"Clip {clipId} has no keyframe_keys — skipping");
            return;
        }

        await EnsureModelLoaded(context);

        // Process all keyframes
        var detections = new List<DetectionResult>();
        foreach (var keyframeKey in keyframeKeys.OrderBy(k => k))
        {
            try
            {
                var response = await _s3Client.GetObjectAsync(_bucketName, keyframeKey);
                using var image = await Image.LoadAsync<Rgb24>(response.ResponseStream);
                var detection = RunClassifier(image, keyframeKey);
                if (detection != null)
                    detections.Add(detection);
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to process {keyframeKey}: {ex.Message}");
            }
        }

        // Compute detection_type from all results
        var detectionType = "none";
        foreach (var d in detections)
            detectionType = UpgradeDetectionType(detectionType, d.Label);

        // Write complete results in a single update (no read-merge-write race)
        await _dynamoClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } },
            UpdateExpression = "SET detection_type = :dt, detection_count = :dc, detections = :dets, inference_at = :ia",
            ConditionExpression = "attribute_exists(clip_id)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":dt"] = new() { S = detectionType },
                [":dc"] = new() { N = detections.Count.ToString() },
                [":dets"] = new() { S = JsonSerializer.Serialize(detections) },
                [":ia"] = new() { S = DateTime.UtcNow.ToString("O") }
            }
        });

        context.Logger.LogInformation(
            $"Clip {clipId}: {detections.Count} detections across {keyframeKeys.Count} keyframes, type={detectionType}");
    }

    private static (string clipId, bool isEventBridge) ParseInput(JsonElement input)
    {
        // EventBridge event has detail.object.key
        if (input.TryGetProperty("detail", out var detail) &&
            detail.TryGetProperty("object", out var obj) &&
            obj.TryGetProperty("key", out var key))
        {
            var keyframeKey = key.GetString()!;
            return (ExtractClipId(keyframeKey), true);
        }

        // Direct invocation has ClipId (case-insensitive)
        if (input.TryGetProperty("ClipId", out var clipIdProp) ||
            input.TryGetProperty("clipId", out clipIdProp))
        {
            return (clipIdProp.GetString()!, false);
        }

        throw new ArgumentException("Unrecognised input: expected EventBridge event or { ClipId: \"...\" }");
    }

    private async Task<Dictionary<string, AttributeValue>?> GetClipRecord(
        string clipId, bool withRetry, ILambdaContext context)
    {
        var maxAttempts = withRetry ? 3 : 1;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var getResponse = await _dynamoClient.GetItemAsync(_tableName,
                new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } });

            if (getResponse.IsItemSet)
                return getResponse.Item;

            if (attempt < maxAttempts - 1)
            {
                context.Logger.LogInformation($"Clip {clipId} not in DynamoDB yet (attempt {attempt + 1}), retrying in 2s...");
                await Task.Delay(2000);
            }
        }

        return null;
    }

    private static string ExtractClipId(string keyframeKey)
    {
        // keyframes/{device}/YYYY/MM/DD/clipId_0001.jpg or keyframes/YYYY/MM/DD/clipId_0001.jpg
        var filename = Path.GetFileNameWithoutExtension(keyframeKey);
        var lastUnderscore = filename.LastIndexOf('_');
        return lastUnderscore > 0 ? filename[..lastUnderscore] : filename;
    }

    private static string UpgradeDetectionType(string current, string candidate)
    {
        // Priority: my_dog(3) > other_dog(2) > none(1) > pending(0)
        var priority = new Dictionary<string, int>
        {
            ["pending"] = 0, ["none"] = 1, ["other_dog"] = 2, ["my_dog"] = 3
        };
        return priority.GetValueOrDefault(candidate, 0) > priority.GetValueOrDefault(current, 0)
            ? candidate
            : current;
    }

    private async Task EnsureModelLoaded(ILambdaContext context)
    {
        if (_classifierSession == null)
        {
            context.Logger.LogInformation("Loading classifier model...");
            var classifierPath = await DownloadModel(_classifierModelKey);
            _classifierSession = new InferenceSession(classifierPath);
        }
    }

    private async Task<string> DownloadModel(string modelKey)
    {
        var localPath = $"/tmp/{Path.GetFileName(modelKey)}";
        if (File.Exists(localPath)) return localPath;

        var response = await _s3Client.GetObjectAsync(_bucketName, modelKey);
        await using var fileStream = File.Create(localPath);
        await response.ResponseStream.CopyToAsync(fileStream);
        return localPath;
    }

    private DetectionResult? RunClassifier(Image<Rgb24> image, string keyframeKey)
    {
        if (_classifierSession == null) return null;

        // Resize to 224x224 for MobileNetV3
        const int targetSize = 224;
        using var resized = image.Clone(ctx => ctx.Resize(targetSize, targetSize));

        // ImageNet normalization
        float[] mean = { 0.485f, 0.456f, 0.406f };
        float[] std = { 0.229f, 0.224f, 0.225f };

        var tensor = new DenseTensor<float>(new[] { 1, 3, targetSize, targetSize });
        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < targetSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < targetSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = (pixel.R / 255f - mean[0]) / std[0];
                    tensor[0, 1, y, x] = (pixel.G / 255f - mean[1]) / std[1];
                    tensor[0, 2, y, x] = (pixel.B / 255f - mean[2]) / std[2];
                }
            }
        });

        var inputName = _classifierSession.InputNames[0];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = _classifierSession.Run(inputs);
        var output = results.First().AsEnumerable<float>().ToArray();

        // Binary classification: ImageFolder sorts alphabetically → [my_dog=0, not_my_dog=1]
        var isMyDog = output.Length >= 2 && output[0] > output[1];
        return new DetectionResult
        {
            KeyframeKey = keyframeKey,
            Label = isMyDog ? "my_dog" : "other_dog",
            Confidence = isMyDog ? output[0] : output[1]
        };
    }
}
