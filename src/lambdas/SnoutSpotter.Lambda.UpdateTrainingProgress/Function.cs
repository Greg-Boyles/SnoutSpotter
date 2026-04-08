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
            exprValues[":progress"] = new() { S = JsonSerializer.Serialize(message.Progress) };
        }

        if (message.Result != null)
        {
            updateExpr += ", #r = :result";
            exprValues[":result"] = new() { S = JsonSerializer.Serialize(message.Result) };
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

        if (message.Status is "downloading" or "training")
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
}
