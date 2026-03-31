using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Amazon.S3;

namespace SnoutSpotter.Api.Services;

public class ExportService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonS3 _s3;
    private readonly string _exportsTable;
    private readonly string _bucketName;
    private readonly string _exportFunctionName;

    public ExportService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _exportsTable = configuration["EXPORTS_TABLE"] ?? "snout-spotter-exports";
        _bucketName = configuration["BUCKET_NAME"]
            ?? throw new InvalidOperationException("BUCKET_NAME not configured");
        _exportFunctionName = configuration["EXPORT_DATASET_FUNCTION"] ?? "snout-spotter-export-dataset";
    }

    public async Task<string> TriggerExportAsync()
    {
        var exportId = Guid.NewGuid().ToString("N");
        var now = DateTime.UtcNow.ToString("O");

        // Create export row with status "running"
        await _dynamoDb.PutItemAsync(_exportsTable, new Dictionary<string, AttributeValue>
        {
            ["export_id"] = new() { S = exportId },
            ["status"] = new() { S = "running" },
            ["created_at"] = new() { S = now },
        });

        // Invoke Lambda async
        var client = new AmazonLambdaClient();
        await client.InvokeAsync(new InvokeRequest
        {
            FunctionName = _exportFunctionName,
            InvocationType = InvocationType.Event,
            Payload = JsonSerializer.Serialize(new { ExportId = exportId })
        });

        return exportId;
    }

    public async Task<List<Dictionary<string, string>>> ListExportsAsync()
    {
        var response = await _dynamoDb.ScanAsync(new ScanRequest
        {
            TableName = _exportsTable
        });

        return response.Items
            .Select(item =>
            {
                var dict = new Dictionary<string, string>();
                foreach (var (k, v) in item)
                {
                    if (v.S != null) dict[k] = v.S;
                    else if (v.N != null) dict[k] = v.N;
                }
                return dict;
            })
            .OrderByDescending(d => d.GetValueOrDefault("created_at", ""))
            .ToList();
    }

    public async Task<string?> GetDownloadUrlAsync(string exportId)
    {
        var result = await _dynamoDb.GetItemAsync(_exportsTable,
            new Dictionary<string, AttributeValue>
            {
                ["export_id"] = new() { S = exportId }
            });

        if (!result.IsItemSet) return null;

        var s3Key = result.Item.GetValueOrDefault("s3_key")?.S;
        if (string.IsNullOrEmpty(s3Key)) return null;

        return _s3.GetPreSignedURL(new Amazon.S3.Model.GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = s3Key,
            Expires = DateTime.UtcNow.AddHours(1)
        });
    }

    public async Task DeleteExportAsync(string exportId)
    {
        // Get S3 key first
        var result = await _dynamoDb.GetItemAsync(_exportsTable,
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
                    await _s3.DeleteObjectAsync(_bucketName, s3Key);
                }
                catch { /* Best effort */ }
            }
        }

        await _dynamoDb.DeleteItemAsync(_exportsTable,
            new Dictionary<string, AttributeValue>
            {
                ["export_id"] = new() { S = exportId }
            });
    }
}
