using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class ModelService : IModelService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonS3 _s3;
    private readonly IS3PresignService _presignService;
    private readonly string _tableName;
    private readonly string _bucketName;

    private static readonly Dictionary<string, (string Prefix, string ActiveKey)> TypePaths = new()
    {
        ["detector"] = ("models/dog-detector/versions/", "models/dog-detector/best.onnx"),
        ["classifier"] = ("models/dog-classifier/versions/", "models/dog-classifier/best.onnx"),
    };

    public ModelService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IS3PresignService presignService, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _presignService = presignService;
        _tableName = config.Value.ModelsTable;
        _bucketName = config.Value.BucketName;
    }

    public async Task<(string? ActiveVersion, List<ModelRecord> Versions)> ListModelsAsync(string type)
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            IndexName = "by-type",
            KeyConditionExpression = "model_type = :type",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":type"] = new() { S = type }
            },
            ScanIndexForward = false
        });

        string? activeVersion = null;
        var versions = new List<ModelRecord>();

        foreach (var item in response.Items)
        {
            var record = FromItem(item);
            versions.Add(record);
            if (record.Status == "active")
                activeVersion = record.Version;
        }

        return (activeVersion, versions);
    }

    public async Task<ModelRecord?> GetModelAsync(string type, string version)
    {
        var modelId = $"{type}#{version}";
        var response = await _dynamoDb.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["model_id"] = new() { S = modelId } });

        return response.IsItemSet ? FromItem(response.Item) : null;
    }

    public async Task<ModelRecord> RegisterModelAsync(RegisterModelRequest request)
    {
        var modelId = $"{request.ModelType}#{request.Version}";
        var now = DateTime.UtcNow.ToString("O");

        var item = new Dictionary<string, AttributeValue>
        {
            ["model_id"] = new() { S = modelId },
            ["model_type"] = new() { S = request.ModelType },
            ["version"] = new() { S = request.Version },
            ["s3_key"] = new() { S = request.S3Key },
            ["size_bytes"] = new() { N = request.SizeBytes.ToString() },
            ["status"] = new() { S = "uploaded" },
            ["created_at"] = new() { S = now },
            ["source"] = new() { S = request.Source },
        };

        if (request.TrainingJobId != null)
            item["training_job_id"] = new() { S = request.TrainingJobId };
        if (request.ExportId != null)
            item["export_id"] = new() { S = request.ExportId };
        if (request.Notes != null)
            item["notes"] = new() { S = request.Notes };
        if (request.Metrics is { Count: > 0 })
            item["metrics"] = new()
            {
                M = request.Metrics.ToDictionary(
                    kv => kv.Key,
                    kv => new AttributeValue { N = kv.Value.ToString("G", CultureInfo.InvariantCulture) })
            };

        await _dynamoDb.PutItemAsync(_tableName, item);

        return FromItem(item);
    }

    public async Task ActivateModelAsync(string type, string version)
    {
        var modelId = $"{type}#{version}";

        // Verify the model exists
        var model = await GetModelAsync(type, version);
        if (model == null)
            throw new InvalidOperationException($"Model {type}/{version} not found");

        // Find current active model and deactivate it
        var (currentActive, _) = await ListModelsAsync(type);
        if (currentActive != null && currentActive != version)
        {
            var oldModelId = $"{type}#{currentActive}";
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue> { ["model_id"] = new() { S = oldModelId } },
                UpdateExpression = "SET #s = :status",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new() { S = "inactive" }
                }
            });
        }

        // Set new model as active
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["model_id"] = new() { S = modelId } },
            UpdateExpression = "SET #s = :status",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new() { S = "active" }
            }
        });

        // Copy to active S3 key for RunInference compatibility
        if (TypePaths.TryGetValue(type, out var paths))
        {
            await _s3.CopyObjectAsync(_bucketName, model.S3Key, _bucketName, paths.ActiveKey);
        }
    }

    public async Task DeleteModelAsync(string type, string version)
    {
        var model = await GetModelAsync(type, version);
        if (model == null)
            throw new InvalidOperationException($"Model {type}/{version} not found");
        if (model.Status == "active")
            throw new InvalidOperationException($"Cannot delete active model {type}/{version}");

        // Delete from DynamoDB
        var modelId = $"{type}#{version}";
        await _dynamoDb.DeleteItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["model_id"] = new() { S = modelId } });

        // Delete from S3
        try
        {
            await _s3.DeleteObjectAsync(_bucketName, model.S3Key);
        }
        catch { /* best effort — S3 object may already be gone */ }
    }

    public async Task<(string UploadUrl, string S3Key)> GetUploadUrlAsync(string type, string version)
    {
        if (!TypePaths.TryGetValue(type, out var paths))
            throw new ArgumentException($"Unknown model type: {type}");

        var s3Key = $"{paths.Prefix}{version}.onnx";
        var uploadUrl = _presignService.GeneratePresignedPutUrl(s3Key, "application/octet-stream");

        // Pre-register the model in DynamoDB (size will be 0 until upload completes)
        await RegisterModelAsync(new RegisterModelRequest(
            ModelType: type,
            Version: version,
            S3Key: s3Key,
            SizeBytes: 0,
            Source: "upload"));

        return (uploadUrl, s3Key);
    }

    private static ModelRecord FromItem(Dictionary<string, AttributeValue> item)
    {
        Dictionary<string, double>? metrics = null;
        if (item.TryGetValue("metrics", out var metricsAttr) && metricsAttr.M is { Count: > 0 })
        {
            metrics = metricsAttr.M.ToDictionary(
                kv => kv.Key,
                kv => double.Parse(kv.Value.N, CultureInfo.InvariantCulture));
        }

        return new ModelRecord(
            ModelId: item.GetValueOrDefault("model_id")?.S ?? "",
            ModelType: item.GetValueOrDefault("model_type")?.S ?? "",
            Version: item.GetValueOrDefault("version")?.S ?? "",
            S3Key: item.GetValueOrDefault("s3_key")?.S ?? "",
            SizeBytes: item.TryGetValue("size_bytes", out var sb) ? long.Parse(sb.N) : 0,
            Status: item.GetValueOrDefault("status")?.S ?? "uploaded",
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "",
            Source: item.GetValueOrDefault("source")?.S ?? "upload",
            TrainingJobId: item.GetValueOrDefault("training_job_id")?.S,
            ExportId: item.GetValueOrDefault("export_id")?.S,
            Notes: item.GetValueOrDefault("notes")?.S,
            Metrics: metrics);
    }
}
