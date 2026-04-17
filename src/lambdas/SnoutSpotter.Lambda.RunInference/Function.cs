using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using SnoutSpotter.Contracts;
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
    private static readonly string[] FallbackClassNames = ["my_dog", "other_dog"];
    private const int CocoClassDog = 16;

    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly string _bucketName;
    private readonly string _tableName;
    private readonly string _singleStageModelKey;
    private readonly string _detectorModelKey;
    private readonly string _classifierModelKey;
    private readonly SettingsReader _settings;

    private InferenceSession? _detectorSession;
    private InferenceSession? _classifierSession;
    private string? _loadedDetectorKey;
    private string? _loadedClassifierKey;
    private string[]? _singleStageClassNames;
    private string[]? _classifierClassNames;

    public Function()
    {
        _s3Client = new AmazonS3Client();
        _dynamoClient = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")!;
        _tableName = Environment.GetEnvironmentVariable("TABLE_NAME")!;
        _singleStageModelKey = Environment.GetEnvironmentVariable("MODEL_KEY") ?? "models/dog-classifier/best.onnx";
        _detectorModelKey = Environment.GetEnvironmentVariable("DETECTOR_MODEL_KEY") ?? "models/yolov8m.onnx";
        _classifierModelKey = Environment.GetEnvironmentVariable("CLASSIFIER_MODEL_KEY") ?? "models/dog-classifier/best.onnx";
        _settings = new SettingsReader(_dynamoClient);
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

        var householdId = clipItem.TryGetValue("household_id", out var hhAttr) ? hhAttr.S : null;

        var pipelineMode = await _settings.GetStringAsync(ServerSettings.InferencePipelineMode);
        var confidenceThreshold = await _settings.GetFloatAsync(ServerSettings.InferenceConfidenceThreshold);
        var inputSize = await _settings.GetIntAsync(ServerSettings.InferenceInputSize);

        var isTwoStage = pipelineMode == "two_stage";

        // Construct household-specific model keys (COCO detector stays global)
        var hhSingleStageModelKey = string.IsNullOrEmpty(householdId)
            ? _singleStageModelKey
            : $"{householdId}/{_singleStageModelKey}";
        var hhClassifierModelKey = string.IsNullOrEmpty(householdId)
            ? _classifierModelKey
            : $"{householdId}/{_classifierModelKey}";

        if (isTwoStage)
        {
            await EnsureDetectorLoaded(_detectorModelKey, context);
            await EnsureClassifierLoaded(hhClassifierModelKey, householdId, context);
        }
        else
        {
            await EnsureDetectorLoaded(hhSingleStageModelKey, context);
        }

        var classifierConfidence = isTwoStage
            ? await _settings.GetFloatAsync(ServerSettings.InferenceClassifierConfidenceThreshold)
            : 0f;
        var classifierInputSize = isTwoStage
            ? await _settings.GetIntAsync(ServerSettings.InferenceClassifierInputSize)
            : 0;
        var cropPadding = isTwoStage
            ? await _settings.GetFloatAsync(ServerSettings.InferenceCropPaddingRatio)
            : 0f;

        var keyframeResults = new List<KeyframeResult>();
        var totalDetections = 0;

        foreach (var keyframeKey in keyframeKeys.OrderBy(k => k))
        {
            try
            {
                var response = await _s3Client.GetObjectAsync(_bucketName, keyframeKey);
                using var image = await Image.LoadAsync<Rgb24>(response.ResponseStream);

                KeyframeResult result;
                if (isTwoStage)
                    result = RunTwoStage(image, keyframeKey, inputSize, confidenceThreshold, classifierInputSize, classifierConfidence, cropPadding);
                else
                    result = RunSingleStage(image, keyframeKey, inputSize, confidenceThreshold);

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
        // SQS event: { "Records": [{ "body": "{\"ClipId\":\"...\"}" }] }
        if (input.TryGetProperty("Records", out var records) && records.GetArrayLength() > 0)
        {
            var body = records[0].GetProperty("body").GetString()!;
            var msg = JsonSerializer.Deserialize<InferenceMessage>(body)
                ?? throw new ArgumentException("Failed to deserialize SQS message body");
            return (msg.ClipId, false);
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

    private static int GetDetectionPriority(string label) =>
        label switch
        {
            "pending" => 0,
            "none" or "no_dog" => 1,
            "other_dog" => 2,
            "my_dog" => 3,
            _ when label.StartsWith("pet-") => 3,
            _ => 0
        };

    private static string UpgradeDetectionType(string current, string candidate) =>
        GetDetectionPriority(candidate) > GetDetectionPriority(current) ? candidate : current;

    private async Task EnsureDetectorLoaded(string modelKey, ILambdaContext context)
    {
        if (_detectorSession != null && _loadedDetectorKey == modelKey) return;
        if (_detectorSession != null && _loadedDetectorKey != modelKey)
        {
            context.Logger.LogInformation($"Detector model changed from {_loadedDetectorKey} to {modelKey} — reloading");
            _detectorSession.Dispose();
            _detectorSession = null;
        }

        context.Logger.LogInformation($"Loading detector model: {modelKey}");
        var modelPath = await DownloadModel(modelKey);
        _detectorSession = new InferenceSession(modelPath);
        _loadedDetectorKey = modelKey;

        // Load class_map.json for single-stage model (alongside best.onnx in the same prefix)
        if (modelKey != _detectorModelKey)
        {
            var classMapKey = modelKey.Replace("best.onnx", "class_map.json")
                .Replace(Path.GetFileName(modelKey), "class_map.json");
            var classMapDir = modelKey[..modelKey.LastIndexOf('/')];
            _singleStageClassNames = await LoadClassMapAsync($"{classMapDir}/class_map.json", context);
        }
    }

    private async Task EnsureClassifierLoaded(string classifierKey, string? householdId, ILambdaContext context)
    {
        if (_classifierSession != null && _loadedClassifierKey == classifierKey) return;
        if (_classifierSession != null && _loadedClassifierKey != classifierKey)
        {
            context.Logger.LogInformation($"Classifier model changed — reloading");
            _classifierSession.Dispose();
            _classifierSession = null;
        }

        context.Logger.LogInformation($"Loading classifier model: {classifierKey}");
        var modelPath = await DownloadModel(classifierKey, householdId);
        _classifierSession = new InferenceSession(modelPath);
        _loadedClassifierKey = classifierKey;

        var classMapDir = classifierKey[..classifierKey.LastIndexOf('/')];
        _classifierClassNames = await LoadClassMapAsync($"{classMapDir}/class_map.json", context);
    }

    private async Task<string[]?> LoadClassMapAsync(string s3Key, ILambdaContext context)
    {
        try
        {
            var response = await _s3Client.GetObjectAsync(_bucketName, s3Key);
            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync();
            var classMap = JsonSerializer.Deserialize<string[]>(json);
            if (classMap is { Length: > 0 })
            {
                context.Logger.LogInformation($"Loaded class_map.json: [{string.Join(", ", classMap)}]");
                return classMap;
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Could not load {s3Key}: {ex.Message} — using fallback class names");
        }
        return null;
    }

    private async Task<string> DownloadModel(string modelKey, string? householdId = null)
    {
        var cachePrefix = string.IsNullOrEmpty(householdId) ? "" : $"{householdId}_";
        var localPath = $"/tmp/{cachePrefix}{Path.GetFileName(modelKey)}";
        if (File.Exists(localPath)) return localPath;

        var response = await _s3Client.GetObjectAsync(_bucketName, modelKey);
        await using var fileStream = File.Create(localPath);
        await response.ResponseStream.CopyToAsync(fileStream);
        return localPath;
    }

    /// <summary>Single-stage: existing two-class YOLO (my_dog, other_dog)</summary>
    private KeyframeResult RunSingleStage(Image<Rgb24> image, string keyframeKey, int inputSize, float confidenceThreshold)
    {
        var result = new KeyframeResult { KeyframeKey = keyframeKey };

        if (_detectorSession == null)
        {
            result.Label = "no_dog";
            return result;
        }

        var detections = RunYolo(_detectorSession, image, inputSize, confidenceThreshold);
        var classNames = _singleStageClassNames ?? FallbackClassNames;

        foreach (var (classIdx, confidence, box) in detections)
        {
            var label = classIdx < classNames.Length ? classNames[classIdx] : $"class_{classIdx}";
            result.Detections.Add(new DetectionBox { Label = label, Confidence = confidence, BoundingBox = box });
        }

        result.Label = "no_dog";
        foreach (var d in result.Detections)
            result.Label = UpgradeDetectionType(result.Label, d.Label);

        return result;
    }

    /// <summary>Two-stage: COCO YOLO detects dogs, classifier identifies my_dog vs other_dog</summary>
    private KeyframeResult RunTwoStage(Image<Rgb24> image, string keyframeKey,
        int detectorInputSize, float detectorConfidence,
        int classifierInputSize, float classifierConfidence, float cropPadding)
    {
        var result = new KeyframeResult { KeyframeKey = keyframeKey };

        if (_detectorSession == null)
        {
            result.Label = "no_dog";
            return result;
        }

        // Stage 1: COCO YOLO — find all dogs
        var allDetections = RunYolo(_detectorSession, image, detectorInputSize, detectorConfidence);
        var dogDetections = allDetections.Where(d => d.ClassIdx == CocoClassDog).ToList();

        if (dogDetections.Count == 0)
        {
            result.Label = "no_dog";
            return result;
        }

        // Stage 2: classify each dog crop
        foreach (var (_, detConf, box) in dogDetections)
        {
            var (classLabel, classConf) = ClassifyCrop(image, box, classifierInputSize, classifierConfidence, cropPadding);
            var combinedConfidence = detConf * classConf;

            result.Detections.Add(new DetectionBox
            {
                Label = classLabel,
                Confidence = combinedConfidence,
                BoundingBox = box
            });
        }

        result.Label = "no_dog";
        foreach (var d in result.Detections)
            result.Label = UpgradeDetectionType(result.Label, d.Label);

        return result;
    }

    private (string Label, float Confidence) ClassifyCrop(Image<Rgb24> image, BoundingBoxData box,
        int inputSize, float confidenceThreshold, float padding)
    {
        if (_classifierSession == null)
            return ("other_dog", 0f);

        // Compute padded crop region
        var padW = box.Width * padding;
        var padH = box.Height * padding;
        var cropX = (int)Math.Max(0, box.X - padW);
        var cropY = (int)Math.Max(0, box.Y - padH);
        var cropW = (int)Math.Min(image.Width - cropX, box.Width + 2 * padW);
        var cropH = (int)Math.Min(image.Height - cropY, box.Height + 2 * padH);

        if (cropW <= 0 || cropH <= 0)
            return ("other_dog", 0f);

        using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH))
                                                .Resize(inputSize, inputSize));

        // Build tensor [1, 3, inputSize, inputSize]
        var tensor = new DenseTensor<float>(new[] { 1, 3, inputSize, inputSize });
        crop.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < inputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < inputSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }
        });

        var inputName = _classifierSession.InputNames[0];
        using var results = _classifierSession.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results.First().AsTensor<float>();

        // Output: [1, N] — logits for each class
        var numClasses = output.Dimensions[1];
        var bestIdx = 0;
        var bestConf = output[0, 0];
        for (var c = 1; c < numClasses; c++)
        {
            if (output[0, c] > bestConf)
            {
                bestIdx = c;
                bestConf = output[0, c];
            }
        }

        // Apply softmax to get probability
        var maxLogit = bestConf;
        for (var c = 0; c < numClasses; c++)
            if (output[0, c] > maxLogit) maxLogit = output[0, c];
        var expSum = 0f;
        for (var c = 0; c < numClasses; c++)
            expSum += MathF.Exp(output[0, c] - maxLogit);
        var softmaxConf = MathF.Exp(output[0, bestIdx] - maxLogit) / expSum;

        var classNames = _classifierClassNames ?? FallbackClassNames;
        var label = bestIdx < classNames.Length ? classNames[bestIdx] : "other_dog";

        // If classifier confidence is below threshold, default to other_dog
        if (softmaxConf < confidenceThreshold)
            return ("other_dog", softmaxConf);

        return (label, softmaxConf);
    }

    /// <summary>Run YOLOv8 and return raw detections with class index, confidence, and bounding box</summary>
    private static List<(int ClassIdx, float Confidence, BoundingBoxData Box)> RunYolo(
        InferenceSession session, Image<Rgb24> image, int inputSize, float confidenceThreshold)
    {
        var detections = new List<(int, float, BoundingBoxData)>();

        using var resized = image.Clone(ctx => ctx.Resize(inputSize, inputSize));

        var tensor = new DenseTensor<float>(new[] { 1, 3, inputSize, inputSize });
        resized.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < inputSize; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < inputSize; x++)
                {
                    var pixel = row[x];
                    tensor[0, 0, y, x] = pixel.R / 255f;
                    tensor[0, 1, y, x] = pixel.G / 255f;
                    tensor[0, 2, y, x] = pixel.B / 255f;
                }
            }
        });

        var inputName = session.InputNames[0];
        using var results = session.Run(new[] { NamedOnnxValue.CreateFromTensor(inputName, tensor) });
        var output = results.First().AsTensor<float>();

        var scaleX = (float)image.Width / inputSize;
        var scaleY = (float)image.Height / inputSize;

        var numClasses = output.Dimensions[1] - 4;
        var numAnchors = output.Dimensions[2];

        for (var i = 0; i < numAnchors; i++)
        {
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

            if (bestConfidence < confidenceThreshold) continue;

            var cx = output[0, 0, i] * scaleX;
            var cy = output[0, 1, i] * scaleY;
            var w = output[0, 2, i] * scaleX;
            var h = output[0, 3, i] * scaleY;

            detections.Add((bestClassIdx, bestConfidence, new BoundingBoxData
            {
                X = Math.Max(0, cx - w / 2),
                Y = Math.Max(0, cy - h / 2),
                Width = w,
                Height = h
            }));
        }

        return detections;
    }
}
