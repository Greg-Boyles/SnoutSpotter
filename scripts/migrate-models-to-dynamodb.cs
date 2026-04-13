#!/usr/bin/env dotnet-script
// Migration script: backfills existing S3 model versions into the snout-spotter-models DynamoDB table.
// Run once after deploying the models table.
//
// Usage: dotnet script scripts/migrate-models-to-dynamodb.cs
//   or compile and run as a standalone console app.
//
// Prerequisites: AWS credentials configured (e.g. via aws configure or environment variables).
// Region defaults to eu-west-1.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Amazon;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;

var region = RegionEndpoint.EUWest1;
var bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")
    ?? throw new Exception("Set BUCKET_NAME env var (e.g. snout-spotter-123456789012)");
var modelsTable = Environment.GetEnvironmentVariable("MODELS_TABLE") ?? "snout-spotter-models";
var trainingJobsTable = Environment.GetEnvironmentVariable("TRAINING_JOBS_TABLE") ?? "snout-spotter-training-jobs";

using var s3 = new AmazonS3Client(region);
using var dynamoDb = new AmazonDynamoDBClient(region);

var typePaths = new Dictionary<string, (string Prefix, string ActiveKey, string ActiveVersionKey)>
{
    ["detector"] = ("models/dog-detector/versions/", "models/dog-detector/best.onnx", "models/dog-detector/active.json"),
    ["classifier"] = ("models/dog-classifier/versions/", "models/dog-classifier/best.onnx", "models/dog-classifier/active.json"),
};

// Index training jobs by model S3 key for backfilling training_job_id + metrics
Console.WriteLine("Scanning training jobs table for completed jobs...");
var jobsByModelKey = new Dictionary<string, Dictionary<string, AttributeValue>>();
string? lastKey = null;
do
{
    var scanRequest = new ScanRequest
    {
        TableName = trainingJobsTable,
        FilterExpression = "attribute_exists(#r.model_s3_key)",
        ExpressionAttributeNames = new Dictionary<string, string> { ["#r"] = "result" },
    };
    if (lastKey != null)
        scanRequest.ExclusiveStartKey = new Dictionary<string, AttributeValue> { ["job_id"] = new() { S = lastKey } };

    var scanResponse = await dynamoDb.ScanAsync(scanRequest);
    foreach (var item in scanResponse.Items)
    {
        if (item.TryGetValue("result", out var resultAttr) && resultAttr.M != null
            && resultAttr.M.TryGetValue("model_s3_key", out var keyAttr))
        {
            jobsByModelKey[keyAttr.S] = item;
        }
    }
    lastKey = scanResponse.LastEvaluatedKey?.GetValueOrDefault("job_id")?.S;
} while (lastKey != null);

Console.WriteLine($"Found {jobsByModelKey.Count} training jobs with model outputs");

var migrated = 0;

foreach (var (modelType, paths) in typePaths)
{
    Console.WriteLine($"\nProcessing {modelType} models...");

    // Get active version from active.json
    string? activeVersion = null;
    try
    {
        var activeObj = await s3.GetObjectAsync(bucketName, paths.ActiveVersionKey);
        using var reader = new System.IO.StreamReader(activeObj.ResponseStream);
        var json = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(json);
        activeVersion = doc.RootElement.GetProperty("version").GetString();
        Console.WriteLine($"  Active version: {activeVersion}");
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        Console.WriteLine("  No active.json found");
    }

    // List all versions in S3
    var listResponse = await s3.ListObjectsV2Async(new ListObjectsV2Request
    {
        BucketName = bucketName,
        Prefix = paths.Prefix
    });

    foreach (var obj in listResponse.S3Objects)
    {
        var fileName = obj.Key[paths.Prefix.Length..];
        if (!fileName.EndsWith(".onnx")) continue;

        // Version may be "v20250413-120345/best.onnx" or "v2.0.onnx"
        var version = fileName.Contains('/')
            ? fileName[..fileName.IndexOf('/')]
            : fileName[..^5]; // strip .onnx

        var modelId = $"{modelType}#{version}";
        var isActive = version == activeVersion;

        // Check if already exists in DynamoDB
        var existing = await dynamoDb.GetItemAsync(modelsTable,
            new Dictionary<string, AttributeValue> { ["model_id"] = new() { S = modelId } });
        if (existing.IsItemSet)
        {
            Console.WriteLine($"  {version} — already exists, skipping");
            continue;
        }

        var item = new Dictionary<string, AttributeValue>
        {
            ["model_id"] = new() { S = modelId },
            ["model_type"] = new() { S = modelType },
            ["version"] = new() { S = version },
            ["s3_key"] = new() { S = obj.Key },
            ["size_bytes"] = new() { N = obj.Size.ToString() },
            ["status"] = new() { S = isActive ? "active" : "inactive" },
            ["created_at"] = new() { S = obj.LastModified.ToUniversalTime().ToString("O") },
            ["source"] = new() { S = "upload" }, // default; overridden below if matched to a training job
        };

        // Try to match to a training job
        if (jobsByModelKey.TryGetValue(obj.Key, out var jobItem))
        {
            item["source"] = new() { S = "training" };
            if (jobItem.TryGetValue("job_id", out var jobId))
                item["training_job_id"] = new() { S = jobId.S };

            // Backfill metrics from training result
            if (jobItem.TryGetValue("result", out var result) && result.M != null)
            {
                var metrics = new Dictionary<string, AttributeValue>();
                var r = result.M;
                if (r.TryGetValue("final_mAP50", out var map50) && map50.N != null)
                    metrics["final_mAP50"] = new() { N = map50.N };
                if (r.TryGetValue("precision", out var prec) && prec.N != null)
                    metrics["precision"] = new() { N = prec.N };
                if (r.TryGetValue("recall", out var rec) && rec.N != null)
                    metrics["recall"] = new() { N = rec.N };
                if (r.TryGetValue("accuracy", out var acc) && acc.N != null)
                    metrics["accuracy"] = new() { N = acc.N };
                if (r.TryGetValue("f1_score", out var f1) && f1.N != null)
                    metrics["f1_score"] = new() { N = f1.N };

                if (metrics.Count > 0)
                    item["metrics"] = new() { M = metrics };
            }

            Console.WriteLine($"  {version} — migrated (source: training, job: {item.GetValueOrDefault("training_job_id")?.S})");
        }
        else
        {
            Console.WriteLine($"  {version} — migrated (source: upload)");
        }

        await dynamoDb.PutItemAsync(modelsTable, item);
        migrated++;
    }
}

Console.WriteLine($"\nMigration complete. {migrated} models migrated.");
