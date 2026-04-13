using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class ExportService : IExportService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonS3 _s3;
    private readonly AppConfig _config;

    public ExportService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _config = config.Value;
    }

    public async Task<string> TriggerExportAsync(int? maxPerClass = null, bool includeBackground = true,
        float backgroundRatio = 1.0f, string exportType = "detection", float cropPadding = 0.1f,
        bool mergeClasses = false)
    {
        var exportId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");

        // Store config as a DynamoDB Map so it's visible while running
        var configMap = new Dictionary<string, AttributeValue>
        {
            ["include_background"] = new() { BOOL = includeBackground },
            ["background_ratio"] = new() { N = backgroundRatio.ToString("F2") },
            ["export_type"] = new() { S = exportType },
        };
        if (maxPerClass.HasValue)
            configMap["max_per_class"] = new() { N = maxPerClass.Value.ToString() };
        if (exportType == "classification")
            configMap["crop_padding"] = new() { N = cropPadding.ToString("F2") };
        if (mergeClasses)
            configMap["merge_classes"] = new() { BOOL = true };

        // Create export row with status "running"
        await _dynamoDb.PutItemAsync(_config.ExportsTable, new Dictionary<string, AttributeValue>
        {
            ["export_id"] = new() { S = exportId },
            ["status"] = new() { S = "running" },
            ["created_at"] = new() { S = now },
            ["config"] = new() { M = configMap },
        });

        // Invoke Lambda async
        var client = new AmazonLambdaClient();
        await client.InvokeAsync(new InvokeRequest
        {
            FunctionName = _config.ExportDatasetFunction,
            InvocationType = InvocationType.Event,
            Payload = JsonSerializer.Serialize(new
            {
                ExportId = exportId,
                MaxPerClass = maxPerClass,
                IncludeBackground = includeBackground,
                BackgroundRatio = backgroundRatio,
                ExportType = exportType,
                CropPadding = cropPadding,
                MergeClasses = mergeClasses,
            })
        });

        return exportId;
    }

    public async Task<List<Dictionary<string, string>>> ListExportsAsync()
    {
        var response = await _dynamoDb.ScanAsync(new ScanRequest
        {
            TableName = _config.ExportsTable
        });

        return response.Items
            .Select(item =>
            {
                var dict = new Dictionary<string, string>();
                foreach (var (k, v) in item)
                {
                    if (v.S != null) dict[k] = v.S;
                    else if (v.N != null) dict[k] = v.N;
                    else if (v.IsBOOLSet) dict[k] = v.BOOL.ToString().ToLowerInvariant();
                    else if (v.M != null && v.M.Count > 0)
                    {
                        var mapDict = new Dictionary<string, object>();
                        foreach (var (mk, mv) in v.M)
                        {
                            if (mv.S != null) mapDict[mk] = mv.S;
                            else if (mv.N != null) mapDict[mk] = double.Parse(mv.N, System.Globalization.CultureInfo.InvariantCulture);
                            else if (mv.IsBOOLSet) mapDict[mk] = mv.BOOL;
                        }
                        dict[k] = JsonSerializer.Serialize(mapDict);
                    }
                }
                return dict;
            })
            .OrderByDescending(d => d.GetValueOrDefault("created_at", ""))
            .ToList();
    }

    public async Task<string?> GetDownloadUrlAsync(string exportId)
    {
        var result = await _dynamoDb.GetItemAsync(_config.ExportsTable,
            new Dictionary<string, AttributeValue>
            {
                ["export_id"] = new() { S = exportId }
            });

        if (!result.IsItemSet) return null;

        var s3Key = result.Item.GetValueOrDefault("s3_key")?.S;
        if (string.IsNullOrEmpty(s3Key)) return null;

        return _s3.GetPreSignedURL(new Amazon.S3.Model.GetPreSignedUrlRequest
        {
            BucketName = _config.BucketName,
            Key = s3Key,
            Expires = DateTime.UtcNow.AddHours(1)
        });
    }

    public async Task DeleteExportAsync(string exportId)
    {
        // Get S3 key first
        var result = await _dynamoDb.GetItemAsync(_config.ExportsTable,
            new Dictionary<string, AttributeValue>
            {
                ["export_id"] = new() { S = exportId }
            });

        if (result.IsItemSet)
        {
            var s3Key = result.Item.GetValueOrDefault("s3_key")?.S;
            if (!string.IsNullOrEmpty(s3Key))
            {
                try
                {
                    await _s3.DeleteObjectAsync(_config.BucketName, s3Key);
                }
                catch { /* Best effort */ }
            }
        }

        await _dynamoDb.DeleteItemAsync(_config.ExportsTable,
            new Dictionary<string, AttributeValue>
            {
                ["export_id"] = new() { S = exportId }
            });
    }
}
