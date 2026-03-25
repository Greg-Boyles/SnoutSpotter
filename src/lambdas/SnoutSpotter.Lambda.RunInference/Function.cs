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

public class InferenceRequest
{
    public string ClipId { get; set; } = string.Empty;
    public List<string> KeyframeKeys { get; set; } = new();
}

public class DetectionResult
{
    public string KeyframeKey { get; set; } = string.Empty;
    public float Confidence { get; set; }
    public float[] BoundingBox { get; set; } = Array.Empty<float>(); // [x1, y1, x2, y2]
    public string Label { get; set; } = string.Empty; // "dog", "my_dog", "other_dog"
}

public class Function
{
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly string _bucketName;
    private readonly string _tableName;
    private readonly string _detectorModelKey;
    private readonly string _classifierModelKey;

    private InferenceSession? _detectorSession;
    private InferenceSession? _classifierSession;

    public Function()
    {
        _s3Client = new AmazonS3Client();
        _dynamoClient = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")!;
        _tableName = Environment.GetEnvironmentVariable("TABLE_NAME")!;
        _detectorModelKey = Environment.GetEnvironmentVariable("DETECTOR_MODEL_KEY")!;
        _classifierModelKey = Environment.GetEnvironmentVariable("CLASSIFIER_MODEL_KEY")!;
    }

    public async Task FunctionHandler(InferenceRequest request, ILambdaContext context)
    {
        context.Logger.LogInformation($"Running inference on clip {request.ClipId} with {request.KeyframeKeys.Count} keyframes");

        // Load models (cached across warm invocations)
        await EnsureModelsLoaded(context);

        var allDetections = new List<DetectionResult>();

        foreach (var keyframeKey in request.KeyframeKeys)
        {
            try
            {
                // Download keyframe
                var response = await _s3Client.GetObjectAsync(_bucketName, keyframeKey);
                using var image = await Image.LoadAsync<Rgb24>(response.ResponseStream);

                // Run dog detector
                var detections = RunDetector(image, keyframeKey);

                foreach (var detection in detections)
                {
                    // For each detected dog, run classifier
                    var cropRegion = new Rectangle(
                        (int)detection.BoundingBox[0],
                        (int)detection.BoundingBox[1],
                        (int)(detection.BoundingBox[2] - detection.BoundingBox[0]),
                        (int)(detection.BoundingBox[3] - detection.BoundingBox[1]));

                    using var crop = image.Clone(ctx => ctx.Crop(cropRegion));
                    var classification = RunClassifier(crop);
                    detection.Label = classification;

                    allDetections.Add(detection);
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogWarning($"Failed to process {keyframeKey}: {ex.Message}");
            }
        }

        // Determine overall detection type
        var detectionType = "none";
        if (allDetections.Any(d => d.Label == "my_dog"))
            detectionType = "my_dog";
        else if (allDetections.Any(d => d.Label == "dog" || d.Label == "other_dog"))
            detectionType = "other_dog";

        // Update DynamoDB with results
        await _dynamoClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["clip_id"] = new() { S = request.ClipId }
            },
            UpdateExpression = "SET detection_type = :dt, detection_count = :dc, detections = :dets, inference_at = :ia",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":dt"] = new() { S = detectionType },
                [":dc"] = new() { N = allDetections.Count.ToString() },
                [":dets"] = new() { S = System.Text.Json.JsonSerializer.Serialize(allDetections) },
                [":ia"] = new() { S = DateTime.UtcNow.ToString("O") }
            }
        });

        context.Logger.LogInformation(
            $"Clip {request.ClipId}: {allDetections.Count} detections, type={detectionType}");
    }

    private async Task EnsureModelsLoaded(ILambdaContext context)
    {
        if (_detectorSession == null)
        {
            context.Logger.LogInformation("Loading detector model...");
            var detectorPath = await DownloadModel(_detectorModelKey);
            _detectorSession = new InferenceSession(detectorPath);
        }

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

    private List<DetectionResult> RunDetector(Image<Rgb24> image, string keyframeKey)
    {
        if (_detectorSession == null) return new List<DetectionResult>();

        // Resize to 640x640 for YOLOv8
        const int targetSize = 640;
        using var resized = image.Clone(ctx => ctx.Resize(targetSize, targetSize));

        // Convert to tensor [1, 3, 640, 640] normalized to [0, 1]
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

        var inputName = _detectorSession.InputNames[0];
        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputName, tensor)
        };

        using var results = _detectorSession.Run(inputs);
        var output = results.First().AsTensor<float>();

        // Parse YOLOv8 output and filter for dog class (class 16 in COCO)
        var detections = new List<DetectionResult>();
        var scaleX = (float)image.Width / targetSize;
        var scaleY = (float)image.Height / targetSize;

        // YOLOv8 output shape: [1, 84, 8400] (84 = 4 bbox + 80 classes)
        var numDetections = output.Dimensions[2];
        for (var i = 0; i < numDetections; i++)
        {
            var dogConfidence = output[0, 4 + 16, i]; // Class 16 = dog in COCO
            if (dogConfidence < 0.5f) continue;

            var cx = output[0, 0, i] * scaleX;
            var cy = output[0, 1, i] * scaleY;
            var w = output[0, 2, i] * scaleX;
            var h = output[0, 3, i] * scaleY;

            detections.Add(new DetectionResult
            {
                KeyframeKey = keyframeKey,
                Confidence = dogConfidence,
                BoundingBox = new[]
                {
                    Math.Max(0, cx - w / 2),
                    Math.Max(0, cy - h / 2),
                    Math.Min(image.Width, cx + w / 2),
                    Math.Min(image.Height, cy + h / 2)
                },
                Label = "dog"
            });
        }

        return detections;
    }

    private string RunClassifier(Image<Rgb24> crop)
    {
        if (_classifierSession == null) return "dog";

        // Resize to 224x224 for MobileNetV3
        const int targetSize = 224;
        using var resized = crop.Clone(ctx => ctx.Resize(targetSize, targetSize));

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
        return output.Length >= 2 && output[1] > output[0] ? "my_dog" : "other_dog";
    }
}
