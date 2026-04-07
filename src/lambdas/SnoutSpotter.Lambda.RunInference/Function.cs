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
    private static readonly string[] ClassNames = ["my_dog", "other_dog"];
    private const int YoloInputSize = 640;
    private const float ConfidenceThreshold = 0.4f;

    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly string _bucketName;
    private readonly string _tableName;
    private readonly string _modelKey;

    private InferenceSession? _session;

    public Function()
    {
        _s3Client = new AmazonS3Client();
        _dynamoClient = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")!;
        _tableName = Environment.GetEnvironmentVariable("TABLE_NAME")!;
        _modelKey = Environment.GetEnvironmentVariable("MODEL_KEY")!;
    }

    public async Task FunctionHandler(JsonElement input, ILambdaContext context)
    {
        var (clipId, isEventBridge) = ParseInput(input);
        context.Logger.LogInformation($"Running inference for clip {clipId} (source={(isEventBridge ? "eventbridge" : "direct")})");

        var clipItem = await GetClipRecord(clipId, isEventBridge, context);
        if (clipItem == null)
        {
            context.Logger.LogWarning($"Clip {clipId} not found — skipping");
            return;
        }

        var keyframeKeys = clipItem.TryGetValue("keyframe_keys", out var kk) ? kk.SS : [];
        if (keyframeKeys.Count == 0)
        {
            context.Logger.LogWarning($"Clip {clipId} has no keyframe_keys — skipping");
            return;
        }

        await EnsureModelLoaded(context);

        var keyframeResults = new List<KeyframeResult>();
        var totalDetections = 0;

        foreach (var keyframeKey in keyframeKeys.OrderBy(k => k))
        {
            try
            {
                var response = await _s3Client.GetObjectAsync(_bucketName, keyframeKey);
                using var image = await Image.LoadAsync<Rgb24>(response.ResponseStream);
                var result = RunDetector(image, keyframeKey);
                keyframeResults.Add(result);
                totalDetections += result.Detections.Count;
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to process {keyframeKey}: {ex.Message}");
            }
        }

        // Compute overall detection_type from keyframe results
        var detectionType = "none";
        foreach (var kr in keyframeResults)
            detectionType = UpgradeDetectionType(detectionType, kr.Label);

        // Build DynamoDB List of Maps for keyframe_detections
        var keyframeDetectionsAttr = new AttributeValue
        {
            L = keyframeResults.Select(kr => new AttributeValue
            {
                M = new Dictionary<string, AttributeValue>
                {
                    ["keyframeKey"] = new() { S = kr.KeyframeKey },
                    ["label"] = new() { S = kr.Label },
                    ["detections"] = new()
                    {
                        L = kr.Detections.Select(d => new AttributeValue
                        {
                            M = new Dictionary<string, AttributeValue>
                            {
                                ["label"] = new() { S = d.Label },
                                ["confidence"] = new() { N = d.Confidence.ToString("F4") },
                                ["boundingBox"] = new()
                                {
                                    M = new Dictionary<string, AttributeValue>
                                    {
                                        ["x"] = new() { N = d.BoundingBox.X.ToString("F1") },
                                        ["y"] = new() { N = d.BoundingBox.Y.ToString("F1") },
                                        ["width"] = new() { N = d.BoundingBox.Width.ToString("F1") },
                                        ["height"] = new() { N = d.BoundingBox.Height.ToString("F1") }
                                    }
                                }
                            }
                        }).ToList()
                    }
                }
            }).ToList()
        };

        await _dynamoClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } },
            UpdateExpression = "SET detection_type = :dt, detection_count = :dc, keyframe_detections = :kd, inference_at = :ia",
            ConditionExpression = "attribute_exists(clip_id)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":dt"] = new() { S = detectionType },
                [":dc"] = new() { N = totalDetections.ToString() },
                [":kd"] = keyframeDetectionsAttr,
                [":ia"] = new() { S = DateTime.UtcNow.ToString("O") }
            }
        });

        context.Logger.LogInformation(
            $"Clip {clipId}: {totalDetections} detections across {keyframeKeys.Count} keyframes, type={detectionType}");
    }

    private static (string clipId, bool isEventBridge) ParseInput(JsonElement input)
    {
        // SQS event: { "Records": [{ "body": "{\"clipId\":\"...\"}" }] }
        if (input.TryGetProperty("Records", out var records) && records.GetArrayLength() > 0)
        {
            var body = records[0].GetProperty("body").GetString()!;
            var msg = JsonDocument.Parse(body).RootElement;
            if (msg.TryGetProperty("clipId", out var sqsClipId) ||
                msg.TryGetProperty("ClipId", out sqsClipId))
            {
                return (sqsClipId.GetString()!, false);
            }
            throw new ArgumentException("SQS message body missing clipId");
        }

        // EventBridge event
        if (input.TryGetProperty("detail", out var detail) &&
            detail.TryGetProperty("object", out var obj) &&
            obj.TryGetProperty("key", out var key))
        {
            return (ExtractClipId(key.GetString()!), true);
        }

        // Direct invocation
        if (input.TryGetProperty("ClipId", out var clipIdProp) ||
            input.TryGetProperty("clipId", out clipIdProp))
        {
            return (clipIdProp.GetString()!, false);
        }

        throw new ArgumentException("Unrecognised input: expected SQS event, EventBridge event, or { ClipId: \"...\" }");
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
        var filename = Path.GetFileNameWithoutExtension(keyframeKey);
        var lastUnderscore = filename.LastIndexOf('_');
        return lastUnderscore > 0 ? filename[..lastUnderscore] : filename;
    }

    private static string UpgradeDetectionType(string current, string candidate)
    {
        var priority = new Dictionary<string, int>
        {
            ["pending"] = 0, ["none"] = 1, ["no_dog"] = 1, ["other_dog"] = 2, ["my_dog"] = 3
        };
        return priority.GetValueOrDefault(candidate, 0) > priority.GetValueOrDefault(current, 0)
            ? candidate
            : current;
    }

    private async Task EnsureModelLoaded(ILambdaContext context)
    {
        if (_session != null) return;

        context.Logger.LogInformation("Loading YOLO model...");
        var modelPath = await DownloadModel(_modelKey);
        _session = new InferenceSession(modelPath);
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

    private KeyframeResult RunDetector(Image<Rgb24> image, string keyframeKey)
    {
        var result = new KeyframeResult { KeyframeKey = keyframeKey };

        if (_session == null)
        {
            result.Label = "no_dog";
            return result;
        }

        using var resized = image.Clone(ctx => ctx.Resize(YoloInputSize, YoloInputSize));

        var tensor = new DenseTensor<float>(new[] { 1, 3, YoloInputSize, YoloInputSize });
        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < YoloInputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < YoloInputSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }
        });

        var inputName = _session.InputNames[0];
        using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results.First().AsTensor<float>();

        var scaleX = (float)image.Width / YoloInputSize;
        var scaleY = (float)image.Height / YoloInputSize;

        // YOLOv8 output: [1, 4+num_classes, 8400]
        var numClasses = output.Dimensions[1] - 4;
        var numDetections = output.Dimensions[2];

        for (var i = 0; i < numDetections; i++)
        {
            // Find best class and its confidence
            var bestClassIdx = 0;
            var bestConfidence = output[0, 4, i];
            for (var c = 1; c < numClasses; c++)
            {
                var conf = output[0, 4 + c, i];
                if (conf > bestConfidence)
                {
                    bestClassIdx = c;
                    bestConfidence = conf;
                }
            }

            if (bestConfidence < ConfidenceThreshold) continue;

            var cx = output[0, 0, i] * scaleX;
            var cy = output[0, 1, i] * scaleY;
            var w = output[0, 2, i] * scaleX;
            var h = output[0, 3, i] * scaleY;

            var label = bestClassIdx < ClassNames.Length ? ClassNames[bestClassIdx] : $"class_{bestClassIdx}";

            result.Detections.Add(new DetectionBox
            {
                Label = label,
                Confidence = bestConfidence,
                BoundingBox = new BoundingBoxData
                {
                    X = Math.Max(0, cx - w / 2),
                    Y = Math.Max(0, cy - h / 2),
                    Width = w,
                    Height = h
                }
            });
        }

        // Overall keyframe label: highest priority detection, or no_dog
        result.Label = "no_dog";
        foreach (var d in result.Detections)
            result.Label = UpgradeDetectionType(result.Label, d.Label);

        return result;
    }
}
