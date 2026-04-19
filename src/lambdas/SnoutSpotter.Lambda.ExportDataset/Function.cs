using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using SnoutSpotter.Contracts;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.ExportDataset;

public class ExportRequest
{
    public string ExportId { get; set; } = string.Empty;
    public int? MaxPerClass { get; set; }
    public bool IncludeBackground { get; set; } = true;
    public float BackgroundRatio { get; set; } = 1.0f;
    public string ExportType { get; set; } = "detection";  // "detection" or "classification"
    public float CropPadding { get; set; } = 0.1f;
    public bool MergeClasses { get; set; } = false;  // When true, all dogs → single "dog" class
    public string? HouseholdId { get; set; }
}

public class Function
{
    private readonly IAmazonS3 _s3;
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _bucketName;
    private readonly string _labelsTable;
    private readonly string _exportsTable;
    private readonly string _petsTable;
    private readonly SettingsReader _settings;
    private float _trainSplitRatio = 0.8f;
    private int _maxParallelDownloads = 20;

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
        _petsTable = Environment.GetEnvironmentVariable("PETS_TABLE") ?? "snout-spotter-pets";
        _settings = new SettingsReader(_dynamoDb);
    }

    public async Task FunctionHandler(ExportRequest request, ILambdaContext context)
    {
        var exportId = request.ExportId;
        context.Logger.LogInformation($"Starting export {exportId} (type={request.ExportType})");

        _trainSplitRatio = await _settings.GetFloatAsync(ServerSettings.ExportTrainSplitRatio);
        _maxParallelDownloads = await _settings.GetIntAsync(ServerSettings.ExportMaxParallelDownloads);

        if (request.ExportType == "classification")
        {
            await RunClassificationExport(request, context);
            return;
        }

        try
        {
            var labels = await GetAllReviewedLabels(request.HouseholdId, context);
            var (classMap, labelToIdx) = await BuildClassMapAsync(request.HouseholdId, context);
            context.Logger.LogInformation($"Found {labels.Count} reviewed labels");

            if (labels.Count == 0)
            {
                await UpdateExportStatus(exportId, "failed", error: "No reviewed labels found");
                return;
            }

            // Only include dog labels that have real bounding box data
            var dogLabelsWithBoxes = labels
                .Where(l => IsKnownPetLabel(l.ConfirmedLabel) && HasBoundingBoxes(l.BoundingBoxes))
                .ToList();
            var skippedByClass = labels
                .Where(l => IsKnownPetLabel(l.ConfirmedLabel) && !HasBoundingBoxes(l.BoundingBoxes))
                .GroupBy(l => l.ConfirmedLabel)
                .ToDictionary(g => g.Key, g => g.Count());
            var dogLabelsNoBoxes = skippedByClass.Values.Sum();
            var noDogLabels = labels.Where(l => l.ConfirmedLabel == "no_dog").ToList();

            context.Logger.LogInformation(
                $"Dogs with boxes: {dogLabelsWithBoxes.Count}, skipped (no boxes): {dogLabelsNoBoxes}, no_dog (background): {noDogLabels.Count}");

            if (dogLabelsWithBoxes.Count == 0)
            {
                await UpdateExportStatus(exportId, "failed", error: "No dog labels with bounding boxes found");
                return;
            }

            var rng = new Random();

            // Apply class balancing if configured
            if (request.MaxPerClass is > 0)
            {
                var target = request.MaxPerClass.Value;
                var byClass = dogLabelsWithBoxes.GroupBy(l => l.ConfirmedLabel)
                    .ToDictionary(g => g.Key, g => g.ToList());

                var balanced = new List<LabelRecord>();
                foreach (var (cls, items) in byClass)
                {
                    context.Logger.LogInformation($"Balancing {cls}: {items.Count} → {target}");
                    balanced.AddRange(BalanceClass(items, target, rng));
                }
                dogLabelsWithBoxes = balanced;
            }

            // Apply background filtering
            if (!request.IncludeBackground)
            {
                noDogLabels.Clear();
            }
            else if (request.BackgroundRatio < 2.0f && noDogLabels.Count > 0)
            {
                var maxBg = (int)(dogLabelsWithBoxes.Count * request.BackgroundRatio);
                if (maxBg < noDogLabels.Count)
                {
                    noDogLabels = noDogLabels.OrderBy(_ => rng.Next()).Take(maxBg).ToList();
                    context.Logger.LogInformation($"Background capped to {noDogLabels.Count} (ratio {request.BackgroundRatio})");
                }
            }

            // 80/20 train/val split (random shuffle)
            dogLabelsWithBoxes = dogLabelsWithBoxes.OrderBy(_ => rng.Next()).ToList();
            noDogLabels = noDogLabels.OrderBy(_ => rng.Next()).ToList();

            var dogTrainCount = (int)(dogLabelsWithBoxes.Count * _trainSplitRatio);
            var noDogTrainCount = (int)(noDogLabels.Count * _trainSplitRatio);

            var trainSet = dogLabelsWithBoxes.Take(dogTrainCount)
                .Concat(noDogLabels.Take(noDogTrainCount)).ToList();
            var valSet = dogLabelsWithBoxes.Skip(dogTrainCount)
                .Concat(noDogLabels.Skip(noDogTrainCount)).ToList();

            var petCounts = dogLabelsWithBoxes
                .GroupBy(l => l.ConfirmedLabel)
                .ToDictionary(g => g.Key, g => g.Count());

            var zipPath = $"/tmp/{exportId}.zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);

            await using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteImageSplit(archive, "train", trainSet, request.MergeClasses, labelToIdx, context);
                await WriteImageSplit(archive, "val", valSet, request.MergeClasses, labelToIdx, context);

                // Write dataset.yaml with dynamic class names
                var yamlSb = new StringBuilder();
                yamlSb.AppendLine("path: .");
                yamlSb.AppendLine("train: images/train");
                yamlSb.AppendLine("val: images/val");
                yamlSb.AppendLine();
                yamlSb.AppendLine("names:");
                if (request.MergeClasses)
                {
                    yamlSb.AppendLine("  0: dog");
                }
                else
                {
                    for (var i = 0; i < classMap.Length; i++)
                        yamlSb.AppendLine($"  {i}: {classMap[i]}");
                }
                var yamlEntry = archive.CreateEntry("dataset.yaml");
                await using (var yamlStream = yamlEntry.Open())
                await using (var writer = new StreamWriter(yamlStream))
                    await writer.WriteAsync(yamlSb.ToString());

                // Write class_map.json — authoritative index-to-label mapping
                var classMapEntry = archive.CreateEntry("class_map.json");
                await using (var classMapStream = classMapEntry.Open())
                    await JsonSerializer.SerializeAsync(classMapStream, classMap);

                // Write manifest.json
                var breedCounts = labels
                    .Where(l => !string.IsNullOrEmpty(l.Breed))
                    .GroupBy(l => l.Breed)
                    .ToDictionary(g => g.Key, g => g.Count());

                var manifest = new
                {
                    export_id = exportId,
                    created_at = DateTime.UtcNow.ToString("O"),
                    format = request.MergeClasses ? "yolo_detection_single_class" : "yolo_detection",
                    total = dogLabelsWithBoxes.Count + noDogLabels.Count,
                    // Legacy fields (set to 0 for new exports, kept for old export UI compat)
                    my_dog = petCounts.GetValueOrDefault("my_dog"),
                    other_dog = petCounts.GetValueOrDefault("other_dog"),
                    no_dog_background = noDogLabels.Count,
                    dogs_without_boxes_skipped = dogLabelsNoBoxes,
                    train_count = trainSet.Count,
                    val_count = valSet.Count,
                    pet_counts = petCounts,
                    class_map = classMap,
                    breeds = breedCounts,
                    config = new
                    {
                        max_per_class = request.MaxPerClass,
                        include_background = request.IncludeBackground,
                        background_ratio = request.BackgroundRatio,
                    },
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

            var exportPrefix = string.IsNullOrEmpty(request.HouseholdId) ? "training-exports" : $"{request.HouseholdId}/training-exports";
            var s3Key = $"{exportPrefix}/{exportId}.zip";
            context.Logger.LogInformation($"Uploading {sizeMb}MB zip to s3://{_bucketName}/{s3Key}");

            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = s3Key,
                FilePath = zipPath,
                ContentType = "application/zip"
            });

            var petCountsAttr = new AttributeValue
            {
                M = petCounts.ToDictionary(kv => kv.Key, kv => new AttributeValue { N = kv.Value.ToString() })
            };

            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _exportsTable,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["export_id"] = new() { S = exportId }
                },
                UpdateExpression = "SET #s = :status, completed_at = :completed, s3_key = :key, " +
                                   "total_images = :total, my_dog_count = :mydog, not_my_dog_count = :notmydog, " +
                                   "no_dog_count = :nodog, " +
                                   "train_count = :train, val_count = :val, size_mb = :size, pet_counts = :pc",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new() { S = "complete" },
                    [":completed"] = new() { S = DateTime.UtcNow.ToString("O") },
                    [":key"] = new() { S = s3Key },
                    [":total"] = new() { N = (dogLabelsWithBoxes.Count + noDogLabels.Count).ToString() },
                    [":mydog"] = new() { N = petCounts.GetValueOrDefault("my_dog").ToString() },
                    [":notmydog"] = new() { N = petCounts.GetValueOrDefault("other_dog").ToString() },
                    [":nodog"]        = new() { N = noDogLabels.Count.ToString() },
                    [":train"] = new() { N = trainSet.Count.ToString() },
                    [":val"] = new() { N = valSet.Count.ToString() },
                    [":size"] = new() { N = sizeMb.ToString() },
                    [":pc"] = petCountsAttr,
                }
            });

            context.Logger.LogInformation($"Export {exportId} complete: {dogLabelsWithBoxes.Count + noDogLabels.Count} images ({dogLabelsWithBoxes.Count} dogs, {noDogLabels.Count} background), {sizeMb}MB");
            File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Export {exportId} failed: {ex.Message}");
            await UpdateExportStatus(exportId, "failed", error: ex.Message);
        }
    }

    private async Task RunClassificationExport(ExportRequest request, ILambdaContext context)
    {
        var exportId = request.ExportId;
        try
        {
            var labels = await GetAllReviewedLabels(request.HouseholdId, context);
            var (classMap, _) = await BuildClassMapAsync(request.HouseholdId, context);
            var dogLabels = labels
                .Where(l => IsKnownPetLabel(l.ConfirmedLabel) && HasBoundingBoxes(l.BoundingBoxes))
                .ToList();

            if (dogLabels.Count == 0)
            {
                await UpdateExportStatus(exportId, "failed", error: "No dog labels with bounding boxes for classification export");
                return;
            }

            var rng = new Random();

            // Balance classes if configured
            if (request.MaxPerClass is > 0)
            {
                var target = request.MaxPerClass.Value;
                var byClass = dogLabels.GroupBy(l => l.ConfirmedLabel).ToDictionary(g => g.Key, g => g.ToList());
                var balanced = new List<LabelRecord>();
                foreach (var (cls, items) in byClass)
                {
                    context.Logger.LogInformation($"Balancing {cls}: {items.Count} → {target}");
                    balanced.AddRange(BalanceClass(items, target, rng));
                }
                dogLabels = balanced;
            }

            // Shuffle and split
            dogLabels = dogLabels.OrderBy(_ => rng.Next()).ToList();
            var trainCount = (int)(dogLabels.Count * _trainSplitRatio);
            var trainSet = dogLabels.Take(trainCount).ToList();
            var valSet = dogLabels.Skip(trainCount).ToList();

            context.Logger.LogInformation($"Classification export: {dogLabels.Count} labels, train={trainSet.Count}, val={valSet.Count}");

            var petCounts = dogLabels
                .GroupBy(l => l.ConfirmedLabel)
                .ToDictionary(g => g.Key, g => g.Count());

            var zipPath = $"/tmp/{exportId}.zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);

            await using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                await WriteClassificationSplit(archive, "train", trainSet, request.CropPadding, context);
                await WriteClassificationSplit(archive, "val", valSet, request.CropPadding, context);

                // Write class_map.json
                var classMapEntry = archive.CreateEntry("class_map.json");
                await using (var cms = classMapEntry.Open())
                    await JsonSerializer.SerializeAsync(cms, classMap);

                var manifest = new
                {
                    export_id = exportId,
                    created_at = DateTime.UtcNow.ToString("O"),
                    format = "classification",
                    total = dogLabels.Count,
                    my_dog = petCounts.GetValueOrDefault("my_dog"),
                    other_dog = petCounts.GetValueOrDefault("other_dog"),
                    pet_counts = petCounts,
                    class_map = classMap,
                    train_count = trainSet.Count,
                    val_count = valSet.Count,
                    config = new
                    {
                        export_type = "classification",
                        max_per_class = request.MaxPerClass,
                        crop_padding = request.CropPadding,
                    },
                };
                var manifestEntry = archive.CreateEntry("manifest.json");
                await using (var ms = manifestEntry.Open())
                    await JsonSerializer.SerializeAsync(ms, manifest, new JsonSerializerOptions { WriteIndented = true });
            }

            var zipSize = new FileInfo(zipPath).Length;
            var sizeMb = Math.Round(zipSize / (1024.0 * 1024.0), 1);
            var exportPrefix = string.IsNullOrEmpty(request.HouseholdId) ? "training-exports" : $"{request.HouseholdId}/training-exports";
            var s3Key = $"{exportPrefix}/{exportId}.zip";

            context.Logger.LogInformation($"Uploading {sizeMb}MB classification zip to s3://{_bucketName}/{s3Key}");
            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName, Key = s3Key, FilePath = zipPath, ContentType = "application/zip"
            });

            var petCountsAttr = new AttributeValue
            {
                M = petCounts.ToDictionary(kv => kv.Key, kv => new AttributeValue { N = kv.Value.ToString() })
            };

            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _exportsTable,
                Key = new Dictionary<string, AttributeValue> { ["export_id"] = new() { S = exportId } },
                UpdateExpression = "SET #s = :status, completed_at = :completed, s3_key = :key, " +
                                   "total_images = :total, my_dog_count = :mydog, not_my_dog_count = :notmydog, " +
                                   "no_dog_count = :nodog, train_count = :train, val_count = :val, size_mb = :size, pet_counts = :pc",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new() { S = "complete" },
                    [":completed"] = new() { S = DateTime.UtcNow.ToString("O") },
                    [":key"] = new() { S = s3Key },
                    [":total"] = new() { N = dogLabels.Count.ToString() },
                    [":mydog"] = new() { N = petCounts.GetValueOrDefault("my_dog").ToString() },
                    [":notmydog"] = new() { N = petCounts.GetValueOrDefault("other_dog").ToString() },
                    [":nodog"] = new() { N = "0" },
                    [":train"] = new() { N = trainSet.Count.ToString() },
                    [":val"] = new() { N = valSet.Count.ToString() },
                    [":size"] = new() { N = sizeMb.ToString() },
                    [":pc"] = petCountsAttr,
                }
            });

            context.Logger.LogInformation($"Classification export {exportId} complete: {dogLabels.Count} crops");
            File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Classification export {exportId} failed: {ex.Message}");
            await UpdateExportStatus(exportId, "failed", error: ex.Message);
        }
    }

    private async Task WriteClassificationSplit(ZipArchive archive, string split, List<LabelRecord> items,
        float cropPadding, ILambdaContext context)
    {
        const int batchSize = 50;
        var cropIdx = 0;

        for (var batchStart = 0; batchStart < items.Count; batchStart += batchSize)
        {
            var batch = items.GetRange(batchStart, Math.Min(batchSize, items.Count - batchStart));
            var downloaded = new (byte[] data, int width, int height)[batch.Count];

            await Parallel.ForEachAsync(
                batch.Select((item, i) => (item, i)),
                new ParallelOptions { MaxDegreeOfParallelism = _maxParallelDownloads },
                async ((LabelRecord item, int i) entry, CancellationToken _) =>
                {
                    try
                    {
                        var response = await _s3.GetObjectAsync(_bucketName, entry.item.KeyframeKey);
                        using var ms = new MemoryStream();
                        await response.ResponseStream.CopyToAsync(ms);
                        ms.Position = 0;
                        var info = await Image.IdentifyAsync(ms);
                        downloaded[entry.i] = (ms.ToArray(), info?.Width ?? 1920, info?.Height ?? 1080);
                    }
                    catch (Exception ex)
                    {
                        context.Logger.LogWarning($"Failed to download {entry.item.KeyframeKey}: {ex.Message}");
                        downloaded[entry.i] = (Array.Empty<byte>(), 0, 0);
                    }
                });

            for (var i = 0; i < batch.Count; i++)
            {
                var (data, imgWidth, imgHeight) = downloaded[i];
                if (data.Length == 0) continue;

                var boxes = ParseBoundingBoxes(batch[i].BoundingBoxes);
                var label = batch[i].ConfirmedLabel;

                using var image = Image.Load<Rgb24>(data);

                foreach (var box in boxes)
                {
                    if (box.Length < 4) continue;

                    var xMin = box[0];
                    var yMin = box[1];
                    var w = box[2];
                    var h = box[3];

                    var padW = w * cropPadding;
                    var padH = h * cropPadding;
                    var cropX = (int)Math.Max(0, xMin - padW);
                    var cropY = (int)Math.Max(0, yMin - padH);
                    var cropW = (int)Math.Min(imgWidth - cropX, w + 2 * padW);
                    var cropH = (int)Math.Min(imgHeight - cropY, h + 2 * padH);

                    if (cropW <= 0 || cropH <= 0) continue;

                    using var crop = image.Clone(ctx => ctx.Crop(new Rectangle(cropX, cropY, cropW, cropH)));
                    using var ms = new MemoryStream();
                    await crop.SaveAsJpegAsync(ms);

                    var entry = archive.CreateEntry($"{split}/{label}/crop_{cropIdx:D5}.jpg");
                    await using var entryStream = entry.Open();
                    ms.Position = 0;
                    await ms.CopyToAsync(entryStream);
                    cropIdx++;
                }
            }
        }

        context.Logger.LogInformation($"Classification {split}: {cropIdx} crops written");
    }

    private async Task WriteImageSplit(ZipArchive archive, string split, List<LabelRecord> items, bool mergeClasses, Dictionary<string, int> labelToIdx, ILambdaContext context)
    {
        const int batchSize = 50;
        var globalIdx = 0;

        for (var batchStart = 0; batchStart < items.Count; batchStart += batchSize)
        {
            var batch = items.GetRange(batchStart, Math.Min(batchSize, items.Count - batchStart));
            var downloaded = new (byte[] data, string ext, int imgWidth, int imgHeight)[batch.Count];

            await Parallel.ForEachAsync(
                batch.Select((item, i) => (item, i)),
                new ParallelOptions { MaxDegreeOfParallelism = _maxParallelDownloads },
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

            for (var i = 0; i < batch.Count; i++, globalIdx++)
            {
                var (data, ext, imgWidth, imgHeight) = downloaded[i];
                if (data.Length == 0) continue;

                var baseName = $"img_{globalIdx:D4}";

                var imgEntry = archive.CreateEntry($"images/{split}/{baseName}{ext}");
                await using (var entryStream = imgEntry.Open())
                    await entryStream.WriteAsync(data);

                var labelContent = ConvertToYoloLabels(items[batchStart + i], imgWidth, imgHeight, mergeClasses, labelToIdx);
                var labelEntry = archive.CreateEntry($"labels/{split}/{baseName}.txt");
                await using (var labelStream = labelEntry.Open())
                await using (var writer = new StreamWriter(labelStream, new UTF8Encoding(false)))
                    await writer.WriteAsync(labelContent);
            }
        }
    }

    private static string ConvertToYoloLabels(LabelRecord label, int imgWidth, int imgHeight, bool mergeClasses, Dictionary<string, int> labelToIdx)
    {
        // no_dog = background image, empty label file
        if (label.ConfirmedLabel == "no_dog")
            return "";

        var classId = mergeClasses ? 0 : labelToIdx.GetValueOrDefault(label.ConfirmedLabel, 0);
        var boxes = ParseBoundingBoxes(label.BoundingBoxes);

        if (boxes.Count == 0)
            return "";

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

    private static List<LabelRecord> BalanceClass(List<LabelRecord> items, int target, Random rng)
    {
        if (items.Count == 0) return items;
        if (items.Count >= target)
            return items.OrderBy(_ => rng.Next()).Take(target).ToList();

        // Oversample: keep all originals, then duplicate random entries to reach target
        var result = new List<LabelRecord>(items);
        while (result.Count < target)
            result.Add(items[rng.Next(items.Count)]);
        return result;
    }

    private static bool HasBoundingBoxes(string json)
    {
        return ParseBoundingBoxes(json).Count > 0;
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

    /// <summary>
    /// Build the class mapping: pet IDs sorted by created_at (class 0..N-1), then "other_dog" as last class.
    /// Returns (classMap array, labelToClassIdx lookup).
    /// </summary>
    private async Task<(string[] ClassMap, Dictionary<string, int> LabelToIdx)> BuildClassMapAsync(string? householdId, ILambdaContext context)
    {
        var petIds = new List<(string PetId, string CreatedAt)>();
        Dictionary<string, AttributeValue>? lastKey = null;
        var hhId = householdId ?? "hh-default";

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _petsTable,
                KeyConditionExpression = "household_id = :hid",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":hid"] = new() { S = hhId }
                },
                ProjectionExpression = "pet_id, created_at",
                ExclusiveStartKey = lastKey
            });

            foreach (var item in response.Items)
            {
                var petId = item["pet_id"].S;
                var createdAt = item.GetValueOrDefault("created_at")?.S ?? "";
                petIds.Add((petId, createdAt));
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey is { Count: > 0 });

        // Sort by created_at for stable class ordering
        petIds.Sort((a, b) => string.Compare(a.CreatedAt, b.CreatedAt, StringComparison.Ordinal));

        // Build class map: pet IDs first, then "other_dog"
        var classMap = petIds.Select(p => p.PetId).Append("other_dog").ToArray();
        var labelToIdx = new Dictionary<string, int>();
        for (var i = 0; i < classMap.Length; i++)
            labelToIdx[classMap[i]] = i;

        // Legacy "my_dog" maps to first pet if available (for backward compat during migration)
        if (petIds.Count > 0 && !labelToIdx.ContainsKey("my_dog"))
            labelToIdx["my_dog"] = 0;

        context.Logger.LogInformation($"Class map: [{string.Join(", ", classMap)}]");
        return (classMap, labelToIdx);
    }

    private static bool IsKnownPetLabel(string label) =>
        label.StartsWith("pet-") || label is "my_dog" or "other_dog";

    private async Task<List<LabelRecord>> GetAllReviewedLabels(string? householdId, ILambdaContext context)
    {
        var labels = new List<LabelRecord>();
        Dictionary<string, AttributeValue>? lastKey = null;

        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":rev"] = new() { S = "true" }
        };
        string? filterExpr = null;
        if (!string.IsNullOrEmpty(householdId))
        {
            filterExpr = "household_id = :hid";
            exprValues[":hid"] = new() { S = householdId };
        }

        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _labelsTable,
                IndexName = "by-review",
                KeyConditionExpression = "reviewed = :rev",
                FilterExpression = filterExpr,
                ExpressionAttributeValues = exprValues,
                ExclusiveStartKey = lastKey
            });

            foreach (var item in response.Items)
            {
                var confirmedLabel = item.GetValueOrDefault("confirmed_label")?.S ?? "";
                if (confirmedLabel != "no_dog" && !IsKnownPetLabel(confirmedLabel)) continue;

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
