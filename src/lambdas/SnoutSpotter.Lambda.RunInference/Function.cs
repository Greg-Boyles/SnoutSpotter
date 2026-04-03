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

    public async Task FunctionHandler(EventBridgeEvent<S3EventDetail> eventBridgeEvent, ILambdaContext context)
    {
        var keyframeKey = eventBridgeEvent.Detail.Object.Key;

        // Only process .jpg keyframes
        if (!keyframeKey.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase))
        {
            context.Logger.LogInformation($"Skipping non-jpg file: {keyframeKey}");
            return;
        }

        var clipId = ExtractClipId(keyframeKey);
        context.Logger.LogInformation($"Running inference on keyframe {keyframeKey} (clip {clipId})");

        await EnsureModelLoaded(context);

        DetectionResult? detection = null;

        try
        {
            var response = await _s3Client.GetObjectAsync(_bucketName, keyframeKey);
            using var image = await Image.LoadAsync<Rgb24>(response.ResponseStream);
            detection = RunClassifier(image, keyframeKey);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Failed to process {keyframeKey}: {ex.Message}");
            return;
        }

        // Wait for IngestClip to write the clip record before updating it.
        // Lambda can fire before IngestClip finishes writing DynamoDB, so retry if not found.
        Dictionary<string, AttributeValue>? existingItem = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var getResponse = await _dynamoClient.GetItemAsync(_tableName,
                new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } });

            if (getResponse.IsItemSet)
            {
                existingItem = getResponse.Item;
                break;
            }

            if (attempt < 2)
            {
                context.Logger.LogInformation($"Clip {clipId} not in DynamoDB yet (attempt {attempt + 1}), retrying in 2s...");
                await Task.Delay(2000);
            }
        }

        if (existingItem == null)
        {
            context.Logger.LogWarning($"Clip {clipId} not found after 3 attempts — skipping update");
            return;
        }

        // Merge this keyframe's result with any already stored
        var existingDetections = new List<DetectionResult>();
        if (existingItem.TryGetValue("detections", out var existing) && existing.S != null)
            existingDetections = JsonSerializer.Deserialize<List<DetectionResult>>(existing.S) ?? [];

        if (detection != null)
            existingDetections.Add(detection);

        // Only upgrade detection_type — my_dog > other_dog > none > pending
        var keyframeType = detection?.Label ?? "none";
        var currentStoredType = existingItem.TryGetValue("detection_type", out var ct) ? ct.S ?? "pending" : "pending";
        var overallDetectionType = UpgradeDetectionType(currentStoredType, keyframeType);

        // ConditionExpression ensures we never create orphan records missing pk/timestamp/date
        await _dynamoClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } },
            UpdateExpression = "SET detection_type = :dt, detection_count = :dc, detections = :dets, inference_at = :ia",
            ConditionExpression = "attribute_exists(clip_id)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":dt"] = new() { S = overallDetectionType },
                [":dc"] = new() { N = existingDetections.Count.ToString() },
                [":dets"] = new() { S = JsonSerializer.Serialize(existingDetections) },
                [":ia"] = new() { S = DateTime.UtcNow.ToString("O") }
            }
        });

        context.Logger.LogInformation(
            $"Keyframe {keyframeKey}: label={detection?.Label ?? "none"} confidence={detection?.Confidence:F2}, clip type={overallDetectionType}");
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

        // Binary classification: [not_my_dog, my_dog]
        var isMyDog = output.Length >= 2 && output[1] > output[0];
        return new DetectionResult
        {
            KeyframeKey = keyframeKey,
            Label = isMyDog ? "my_dog" : "other_dog",
            Confidence = isMyDog ? output[1] : output[0]
        };
    }
}
