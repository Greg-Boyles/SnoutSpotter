using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.ExportDataset;

public class ExportRequest
{
    public string ExportId { get; set; } = string.Empty;
}

public class Function
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _bucketName;
    private readonly string _labelsTable;
    private readonly string _exportsTable;

    public Function()
    {
        _s3 = new AmazonS3Client();
        _dynamoDb = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")
            ?? throw new InvalidOperationException("BUCKET_NAME not set");
        _labelsTable = Environment.GetEnvironmentVariable("LABELS_TABLE")
            ?? throw new InvalidOperationException("LABELS_TABLE not set");
        _exportsTable = Environment.GetEnvironmentVariable("EXPORTS_TABLE")
            ?? throw new InvalidOperationException("EXPORTS_TABLE not set");
    }

    public async Task FunctionHandler(ExportRequest request, ILambdaContext context)
    {
        var exportId = request.ExportId;
        context.Logger.LogInformation($"Starting export {exportId}");

        try
        {
            var labels = await GetAllReviewedLabels(context);
            context.Logger.LogInformation($"Found {labels.Count} reviewed labels");

            if (labels.Count == 0)
            {
                await UpdateExportStatus(exportId, "failed", error: "No reviewed labels found");
                return;
            }

            // For YOLO detection training: my_dog (class 0), other_dog (class 1)
            // Skip no_dog labels — absence of detections IS the no_dog signal
            var dogLabels = labels.Where(l => l.ConfirmedLabel is "my_dog" or "other_dog").ToList();
            var noDogCount = labels.Count(l => l.ConfirmedLabel == "no_dog");

            if (dogLabels.Count == 0)
            {
                await UpdateExportStatus(exportId, "failed", error: "No dog labels with bounding boxes found");
                return;
            }

            // 80/20 train/val split (random shuffle)
            var rng = new Random();
            dogLabels = dogLabels.OrderBy(_ => rng.Next()).ToList();
            var trainCount = (int)(dogLabels.Count * 0.8);
            var trainSet = dogLabels.Take(trainCount).ToList();
            var valSet = dogLabels.Skip(trainCount).ToList();

            var zipPath = $"/tmp/{exportId}.zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);

            await using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteImageSplit(archive, "train", trainSet, context);
                await WriteImageSplit(archive, "val", valSet, context);

                // Write dataset.yaml
                var datasetYaml = """
                    path: .
                    train: images/train
                    val: images/val

                    names:
                      0: my_dog
                      1: other_dog
                    """;
                var yamlEntry = archive.CreateEntry("dataset.yaml");
                await using (var yamlStream = yamlEntry.Open())
                await using (var writer = new StreamWriter(yamlStream))
                    await writer.WriteAsync(datasetYaml);

                // Write manifest.json
                var breedCounts = labels
                    .Where(l => !string.IsNullOrEmpty(l.Breed))
                    .GroupBy(l => l.Breed)
                    .ToDictionary(g => g.Key, g => g.Count());

                var myDogCount = dogLabels.Count(l => l.ConfirmedLabel == "my_dog");
                var otherDogCount = dogLabels.Count(l => l.ConfirmedLabel == "other_dog");

                var manifest = new
                {
                    export_id = exportId,
                    created_at = DateTime.UtcNow.ToString("O"),
                    format = "yolo_detection",
                    total = dogLabels.Count,
                    my_dog = myDogCount,
                    other_dog = otherDogCount,
                    no_dog_excluded = noDogCount,
                    train_count = trainCount,
                    val_count = dogLabels.Count - trainCount,
                    breeds = breedCounts,
                };
                var manifestEntry = archive.CreateEntry("manifest.json");
                await using (var manifestStream = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });

                // Write labels.csv for reference
                var csvLines = new List<string> { "filename,split,confirmed_label,breed" };
                AddCsvLines(csvLines, "train", trainSet);
                AddCsvLines(csvLines, "val", valSet);
                var csvEntry = archive.CreateEntry("labels.csv");
                await using var csvStream = csvEntry.Open();
                await using var csvWriter = new StreamWriter(csvStream);
                foreach (var line in csvLines)
                    await csvWriter.WriteLineAsync(line);
            }

            var zipSize = new FileInfo(zipPath).Length;
            var sizeMb = Math.Round(zipSize / (1024.0 * 1024.0), 1);

            var s3Key = $"training-exports/{exportId}.zip";
            context.Logger.LogInformation($"Uploading {sizeMb}MB zip to s3://{_bucketName}/{s3Key}");

            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = s3Key,
                FilePath = zipPath,
                ContentType = "application/zip"
            });

            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _exportsTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["export_id"] = new() { S = exportId }
                },
                UpdateExpression = "SET #s = :status, completed_at = :completed, s3_key = :key, " +
                                   "total_images = :total, my_dog_count = :mydog, not_my_dog_count = :notmydog, " +
                                   "train_count = :train, val_count = :val, size_mb = :size",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new() { S = "complete" },
                    [":completed"] = new() { S = DateTime.UtcNow.ToString("O") },
                    [":key"] = new() { S = s3Key },
                    [":total"] = new() { N = dogLabels.Count.ToString() },
                    [":mydog"] = new() { N = dogLabels.Count(l => l.ConfirmedLabel == "my_dog").ToString() },
                    [":notmydog"] = new() { N = dogLabels.Count(l => l.ConfirmedLabel == "other_dog").ToString() },
                    [":train"] = new() { N = trainCount.ToString() },
                    [":val"] = new() { N = (dogLabels.Count - trainCount).ToString() },
                    [":size"] = new() { N = sizeMb.ToString() },
                }
            });

            context.Logger.LogInformation($"Export {exportId} complete: {dogLabels.Count} images, {sizeMb}MB");
            File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Export {exportId} failed: {ex.Message}");
            await UpdateExportStatus(exportId, "failed", error: ex.Message);
        }
    }

    private async Task WriteImageSplit(ZipArchive archive, string split, List<LabelRecord> items, ILambdaContext context)
    {
        // Parallel-download all images
        var downloaded = new (byte[] data, string ext, int imgWidth, int imgHeight)[items.Count];
        await Parallel.ForEachAsync(
            items.Select((item, i) => (item, i)),
            new ParallelOptions { MaxDegreeOfParallelism = 20 },
            async ((LabelRecord item, int i) entry, CancellationToken _) =>
            {
                try
                {
                    var response = await _s3.GetObjectAsync(_bucketName, entry.item.KeyframeKey);
                    using var ms = new MemoryStream();
                    await response.ResponseStream.CopyToAsync(ms);
                    ms.Position = 0;
                    var imageInfo = await Image.IdentifyAsync(ms);
                    var ext = Path.GetExtension(entry.item.KeyframeKey).ToLowerInvariant();
                    downloaded[entry.i] = (ms.ToArray(), string.IsNullOrEmpty(ext) ? ".jpg" : ext,
                        imageInfo?.Width ?? 1920, imageInfo?.Height ?? 1080);
                }
                catch (Exception ex)
                {
                    context.Logger.LogWarning($"Failed to download {entry.item.KeyframeKey}: {ex.Message}");
                    downloaded[entry.i] = (Array.Empty<byte>(), ".jpg", 1920, 1080);
                }
            });

        // Write images and YOLO label files sequentially
        for (var i = 0; i < downloaded.Length; i++)
        {
            var (data, ext, imgWidth, imgHeight) = downloaded[i];
            if (data.Length == 0) continue;

            var baseName = $"img_{i:D4}";

            // Write image
            var imgEntry = archive.CreateEntry($"images/{split}/{baseName}{ext}");
            await using (var entryStream = imgEntry.Open())
                await entryStream.WriteAsync(data);

            // Write YOLO label file
            var labelContent = ConvertToYoloLabels(items[i], imgWidth, imgHeight);
            var labelEntry = archive.CreateEntry($"labels/{split}/{baseName}.txt");
            await using (var labelStream = labelEntry.Open())
            await using (var writer = new StreamWriter(labelStream, new UTF8Encoding(false)))
                await writer.WriteAsync(labelContent);
        }
    }

    private static string ConvertToYoloLabels(LabelRecord label, int imgWidth, int imgHeight)
    {
        var classId = label.ConfirmedLabel == "my_dog" ? 0 : 1;
        var boxes = ParseBoundingBoxes(label.BoundingBoxes);

        if (boxes.Count == 0)
        {
            // No bounding boxes — use full image as a single detection
            return $"{classId} 0.5 0.5 1.0 1.0";
        }

        var sb = new StringBuilder();
        foreach (var box in boxes)
        {
            // box = [x_min, y_min, width, height, confidence]
            var xMin = box.Length > 0 ? box[0] : 0;
            var yMin = box.Length > 1 ? box[1] : 0;
            var w = box.Length > 2 ? box[2] : imgWidth;
            var h = box.Length > 3 ? box[3] : imgHeight;

            // Convert to YOLO normalized center format
            var cx = (xMin + w / 2) / imgWidth;
            var cy = (yMin + h / 2) / imgHeight;
            var nw = w / imgWidth;
            var nh = h / imgHeight;

            // Clamp to [0, 1]
            cx = Math.Clamp(cx, 0f, 1f);
            cy = Math.Clamp(cy, 0f, 1f);
            nw = Math.Clamp(nw, 0f, 1f);
            nh = Math.Clamp(nh, 0f, 1f);

            if (sb.Length > 0) sb.AppendLine();
            sb.Append($"{classId} {cx:F6} {cy:F6} {nw:F6} {nh:F6}");
        }

        return sb.ToString();
    }

    private static List<float[]> ParseBoundingBoxes(string json)
    {
        if (string.IsNullOrEmpty(json) || json == "[]") return [];
        try
        {
            return JsonSerializer.Deserialize<List<float[]>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void AddCsvLines(List<string> csvLines, string split, List<LabelRecord> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            var ext = Path.GetExtension(items[i].KeyframeKey).ToLowerInvariant();
            if (string.IsNullOrEmpty(ext)) ext = ".jpg";
            csvLines.Add($"images/{split}/img_{i:D4}{ext},{split},{items[i].ConfirmedLabel},{items[i].Breed}");
        }
    }

    private async Task<List<LabelRecord>> GetAllReviewedLabels(ILambdaContext context)
    {
        var labels = new List<LabelRecord>();
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
                ExclusiveStartKey = lastKey
            });

            foreach (var item in response.Items)
            {
                var confirmedLabel = item.GetValueOrDefault("confirmed_label")?.S ?? "";
                if (confirmedLabel is not ("my_dog" or "other_dog" or "no_dog")) continue;

                labels.Add(new LabelRecord
                {
                    KeyframeKey = item["keyframe_key"].S,
                    ConfirmedLabel = confirmedLabel,
                    Breed = item.GetValueOrDefault("breed")?.S ?? "",
                    BoundingBoxes = item.GetValueOrDefault("bounding_boxes")?.S ?? "[]"
                });
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey != null && lastKey.Count > 0);

        return labels;
    }

    private async Task UpdateExportStatus(string exportId, string status, string error = "")
    {
        var updateExpr = "SET #s = :status, completed_at = :completed";
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":status"] = new() { S = status },
            [":completed"] = new() { S = DateTime.UtcNow.ToString("O") },
        };

        if (!string.IsNullOrEmpty(error))
        {
            updateExpr += ", error = :error";
            exprValues[":error"] = new() { S = error };
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _exportsTable,
            Key = new Dictionary<string, AttributeValue>
            {
                ["export_id"] = new() { S = exportId }
            },
            UpdateExpression = updateExpr,
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = exprValues
        });
    }

    private record LabelRecord
    {
        public string KeyframeKey { get; init; } = "";
        public string ConfirmedLabel { get; init; } = "";
        public string Breed { get; init; } = "";
        public string BoundingBoxes { get; init; } = "[]";
    }
}
