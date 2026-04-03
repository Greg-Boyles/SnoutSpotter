using System.IO.Compression;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;

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
            // Query all reviewed labels
            var labels = await GetAllReviewedLabels(context);
            context.Logger.LogInformation($"Found {labels.Count} reviewed labels");

            if (labels.Count == 0)
            {
                await UpdateExportStatus(exportId, "failed", error: "No reviewed labels found");
                return;
            }

            // Split into my_dog and not_my_dog (other_dog + no_dog both go into not_my_dog)
            var myDog = labels.Where(l => l.confirmedLabel == "my_dog").ToList();
            var notMyDog = labels.Where(l => l.confirmedLabel is "other_dog" or "no_dog").ToList();

            // 80/20 train/val split (random shuffle)
            var rng = new Random();
            myDog = myDog.OrderBy(_ => rng.Next()).ToList();
            notMyDog = notMyDog.OrderBy(_ => rng.Next()).ToList();

            var myDogTrainCount = (int)(myDog.Count * 0.8);
            var notMyDogTrainCount = (int)(notMyDog.Count * 0.8);

            var splits = new Dictionary<string, List<LabelRecord>>
            {
                ["train/my_dog"] = myDog.Take(myDogTrainCount).ToList(),
                ["val/my_dog"] = myDog.Skip(myDogTrainCount).ToList(),
                ["train/not_my_dog"] = notMyDog.Take(notMyDogTrainCount).ToList(),
                ["val/not_my_dog"] = notMyDog.Skip(notMyDogTrainCount).ToList(),
            };

            var trainCount = myDogTrainCount + notMyDogTrainCount;
            var valCount = labels.Count - trainCount;

            // Stream images directly into zip — no intermediate directory needed
            var zipPath = $"/tmp/{exportId}.zip";
            if (File.Exists(zipPath)) File.Delete(zipPath);

            await using (var zipStream = File.Create(zipPath))
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var (splitDir, items) in splits)
                {
                    // Parallel-download all images for this split into memory
                    var downloaded = new (byte[] data, string ext)[items.Count];
                    await Parallel.ForEachAsync(
                        items.Select((item, i) => (item, i)),
                        new ParallelOptions { MaxDegreeOfParallelism = 20 },
                        async ((LabelRecord item, int i) entry, CancellationToken _) =>
                        {
                            try
                            {
                                var response = await _s3.GetObjectAsync(_bucketName, entry.item.keyframeKey);
                                using var ms = new MemoryStream();
                                await response.ResponseStream.CopyToAsync(ms);
                                var ext = Path.GetExtension(entry.item.keyframeKey).ToLowerInvariant();
                                downloaded[entry.i] = (ms.ToArray(), string.IsNullOrEmpty(ext) ? ".jpg" : ext);
                            }
                            catch (Exception ex)
                            {
                                context.Logger.LogWarning($"Failed to download {entry.item.keyframeKey}: {ex.Message}");
                                downloaded[entry.i] = (Array.Empty<byte>(), ".jpg");
                            }
                        });

                    // Write sequentially into the archive (ZipArchive is not thread-safe)
                    for (var i = 0; i < downloaded.Length; i++)
                    {
                        var (data, ext) = downloaded[i];
                        if (data.Length == 0) continue;
                        var zipEntry = archive.CreateEntry($"{splitDir}/img_{i:D4}{ext}");
                        await using var entryStream = zipEntry.Open();
                        await entryStream.WriteAsync(data);
                    }
                }

                // Write manifest.json as archive entry
                var breedCounts = labels
                    .Where(l => !string.IsNullOrEmpty(l.breed))
                    .GroupBy(l => l.breed)
                    .ToDictionary(g => g.Key, g => g.Count());

                var manifest = new
                {
                    export_id = exportId,
                    created_at = DateTime.UtcNow.ToString("O"),
                    total = labels.Count,
                    my_dog = myDog.Count,
                    not_my_dog = notMyDog.Count,
                    train_count = trainCount,
                    val_count = valCount,
                    breeds = breedCounts,
                };
                var manifestEntry = archive.CreateEntry("manifest.json");
                await using (var manifestStream = manifestEntry.Open())
                {
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, new JsonSerializerOptions { WriteIndented = true });
                }

                // Write labels.csv as archive entry
                var csvLines = new List<string> { "filename,split,confirmed_label,breed" };
                foreach (var (splitDir, items) in splits)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        var ext = Path.GetExtension(items[i].keyframeKey).ToLowerInvariant();
                        if (string.IsNullOrEmpty(ext)) ext = ".jpg";
                        csvLines.Add($"{splitDir}/img_{i:D4}{ext},{splitDir.Split('/')[0]},{items[i].confirmedLabel},{items[i].breed}");
                    }
                }
                var csvEntry = archive.CreateEntry("labels.csv");
                await using var csvStream = csvEntry.Open();
                await using var csvWriter = new StreamWriter(csvStream);
                foreach (var line in csvLines)
                    await csvWriter.WriteLineAsync(line);
            }

            var zipSize = new FileInfo(zipPath).Length;
            var sizeMb = Math.Round(zipSize / (1024.0 * 1024.0), 1);

            // Upload zip to S3
            var s3Key = $"training-exports/{exportId}.zip";
            context.Logger.LogInformation($"Uploading {sizeMb}MB zip to s3://{_bucketName}/{s3Key}");

            await _s3.PutObjectAsync(new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = s3Key,
                FilePath = zipPath,
                ContentType = "application/zip"
            });

            // Update export row
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
                    [":total"] = new() { N = labels.Count.ToString() },
                    [":mydog"] = new() { N = myDog.Count.ToString() },
                    [":notmydog"] = new() { N = notMyDog.Count.ToString() },
                    [":train"] = new() { N = trainCount.ToString() },
                    [":val"] = new() { N = valCount.ToString() },
                    [":size"] = new() { N = sizeMb.ToString() },
                }
            });

            context.Logger.LogInformation($"Export {exportId} complete: {labels.Count} images, {sizeMb}MB");

            // Cleanup
            File.Delete(zipPath);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Export {exportId} failed: {ex.Message}");
            await UpdateExportStatus(exportId, "failed", error: ex.Message);
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
                    keyframeKey = item["keyframe_key"].S,
                    confirmedLabel = confirmedLabel,
                    breed = item.GetValueOrDefault("breed")?.S ?? ""
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
        public string keyframeKey { get; init; } = "";
        public string confirmedLabel { get; init; } = "";
        public string breed { get; init; } = "";
    }
}
