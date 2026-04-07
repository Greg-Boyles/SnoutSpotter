using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

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
        var jobId = input.GetProperty("job_id").GetString()!;
        var status = input.GetProperty("status").GetString()!;
        var now = DateTime.UtcNow.ToString("O");

        var updateExpr = "SET #s = :status, updated_at = :now";
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":status"] = new() { S = status },
            [":now"] = new() { S = now }
        };
        var exprNames = new Dictionary<string, string> { ["#s"] = "status" };

        // Write progress if present
        if (input.TryGetProperty("progress", out var progress))
        {
            updateExpr += ", progress = :progress";
            exprValues[":progress"] = new() { S = progress.GetRawText() };
        }

        // Write result if present (terminal status)
        if (input.TryGetProperty("result", out var result))
        {
            updateExpr += ", #r = :result";
            exprValues[":result"] = new() { S = result.GetRawText() };
            exprNames["#r"] = "result";
        }

        // Write checkpoint if present
        if (input.TryGetProperty("checkpoint_s3_key", out var checkpoint))
        {
            updateExpr += ", checkpoint_s3_key = :checkpoint";
            exprValues[":checkpoint"] = new() { S = checkpoint.GetString()! };
        }

        // Write error if present
        if (input.TryGetProperty("error", out var error))
        {
            updateExpr += ", #e = :error";
            exprValues[":error"] = new() { S = error.GetString()! };
            exprNames["#e"] = "error";
        }

        // Set started_at on first non-pending status
        if (status is "downloading" or "training")
        {
            updateExpr += ", started_at = if_not_exists(started_at, :now)";
        }

        // Set completed_at on terminal statuses
        if (status is "complete" or "failed" or "cancelled" or "interrupted")
        {
            updateExpr += ", completed_at = :now";
        }

        await _dynamoClient.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["job_id"] = new() { S = jobId }
            },
            UpdateExpression = updateExpr,
            ExpressionAttributeValues = exprValues,
            ExpressionAttributeNames = exprNames,
            ConditionExpression = "attribute_exists(job_id)"
        });

        context.Logger.LogInformation($"Updated job {jobId}: status={status}");
    }
}
