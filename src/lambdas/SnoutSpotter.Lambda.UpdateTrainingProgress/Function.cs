using System.Globalization;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using SnoutSpotter.Shared.Training;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.UpdateTrainingProgress;

/// <summary>
/// Receives training progress messages from IoT Rule (MQTT topic:
/// snoutspotter/trainer/+/progress) and patches the training-jobs
/// DynamoDB table with status, progress, and result fields.
/// </summary>
public class Function
{
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly string _tableName;

    public Function()
    {
        _dynamoClient = new AmazonDynamoDBClient();
        _tableName = Environment.GetEnvironmentVariable("TRAINING_JOBS_TABLE")
            ?? "snout-spotter-training-jobs";
    }

    public async Task FunctionHandler(JsonElement input, ILambdaContext context)
    {
        var message = JsonSerializer.Deserialize<TrainingProgressMessage>(input.GetRawText())
            ?? throw new InvalidOperationException("Failed to deserialise TrainingProgressMessage");

        var now = DateTime.UtcNow.ToString("O");

        var updateExpr = "SET #s = :status, updated_at = :now";
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":status"] = new() { S = message.Status },
            [":now"]    = new() { S = now }
        };
        var exprNames = new Dictionary<string, string> { ["#s"] = "status" };

        if (message.Progress != null)
        {
            updateExpr += ", progress = :progress";
            exprValues[":progress"] = new() { M = ToMap(message.Progress) };
        }

        if (message.Result != null)
        {
            updateExpr += ", #r = :result";
            exprValues[":result"] = new() { M = ToMap(message.Result) };
            exprNames["#r"] = "result";
        }

        if (message.CheckpointS3Key != null)
        {
            updateExpr += ", checkpoint_s3_key = :checkpoint";
            exprValues[":checkpoint"] = new() { S = message.CheckpointS3Key };
        }

        if (message.Error != null)
        {
            updateExpr += ", #e = :error";
            exprValues[":error"] = new() { S = message.Error };
            exprNames["#e"] = "error";
        }

        if (message.FailedStage != null)
        {
            updateExpr += ", failed_stage = :failed_stage";
            exprValues[":failed_stage"] = new() { S = message.FailedStage };
        }

        if (message.Status is "downloading" or "scanning" or "training")
        {
            updateExpr += ", started_at = if_not_exists(started_at, :now)";
        }

        if (message.Status is "complete" or "failed" or "cancelled" or "interrupted")
        {
            updateExpr += ", completed_at = :now";
        }

        await _dynamoClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["job_id"] = new() { S = message.JobId }
            },
            UpdateExpression = updateExpr,
            ExpressionAttributeValues = exprValues,
            ExpressionAttributeNames = exprNames,
            ConditionExpression = "attribute_exists(job_id)"
        });

        context.Logger.LogInformation($"Updated job {message.JobId}: status={message.Status}");
    }

    private static Dictionary<string, AttributeValue> ToMap(TrainingProgress p)
    {
        var m = new Dictionary<string, AttributeValue>
        {
            ["epoch"]        = new() { N = p.Epoch.ToString() },
            ["total_epochs"] = new() { N = p.TotalEpochs.ToString() },
        };
        if (p.TrainLoss.HasValue)         m["train_loss"]          = new() { N = p.TrainLoss.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (p.ValLoss.HasValue)           m["val_loss"]            = new() { N = p.ValLoss.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (p.MAP50.HasValue)             m["mAP50"]               = new() { N = p.MAP50.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (p.MAP50_95.HasValue)          m["mAP50_95"]            = new() { N = p.MAP50_95.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (p.BestMAP50.HasValue)         m["best_mAP50"]          = new() { N = p.BestMAP50.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (p.ElapsedSeconds.HasValue)    m["elapsed_seconds"]     = new() { N = p.ElapsedSeconds.Value.ToString() };
        if (p.EtaSeconds.HasValue)        m["eta_seconds"]         = new() { N = p.EtaSeconds.Value.ToString() };
        if (p.GpuUtilPercent.HasValue)    m["gpu_util_percent"]    = new() { N = p.GpuUtilPercent.Value.ToString() };
        if (p.GpuTempC.HasValue)          m["gpu_temp_c"]          = new() { N = p.GpuTempC.Value.ToString() };
        if (p.EpochProgress.HasValue)    m["epoch_progress"]      = new() { N = p.EpochProgress.Value.ToString() };
        if (p.DownloadBytes.HasValue)     m["download_bytes"]      = new() { N = p.DownloadBytes.Value.ToString() };
        if (p.DownloadTotalBytes.HasValue) m["download_total_bytes"] = new() { N = p.DownloadTotalBytes.Value.ToString() };
        if (p.DownloadSpeedMbps.HasValue) m["download_speed_mbps"] = new() { N = p.DownloadSpeedMbps.Value.ToString("G", CultureInfo.InvariantCulture) };
        return m;
    }

    private static Dictionary<string, AttributeValue> ToMap(TrainingResult r)
    {
        var m = new Dictionary<string, AttributeValue>
        {
            ["model_s3_key"]          = new() { S = r.ModelS3Key },
            ["model_size_mb"]         = new() { N = r.ModelSizeMb.ToString("G", CultureInfo.InvariantCulture) },
            ["final_mAP50"]           = new() { N = r.FinalMAP50.ToString("G", CultureInfo.InvariantCulture) },
            ["total_epochs"]          = new() { N = r.TotalEpochs.ToString() },
            ["best_epoch"]            = new() { N = r.BestEpoch.ToString() },
            ["training_time_seconds"] = new() { N = r.TrainingTimeSeconds.ToString() },
            ["dataset_images"]        = new() { N = r.DatasetImages.ToString() },
            ["classes"]               = new() { L = r.Classes.Select(c => new AttributeValue { S = c }).ToList() },
        };
        if (r.FinalMAP50_95.HasValue) m["final_mAP50_95"] = new() { N = r.FinalMAP50_95.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (r.Precision.HasValue)     m["precision"]       = new() { N = r.Precision.Value.ToString("G", CultureInfo.InvariantCulture) };
        if (r.Recall.HasValue)        m["recall"]          = new() { N = r.Recall.Value.ToString("G", CultureInfo.InvariantCulture) };
        return m;
    }
}
