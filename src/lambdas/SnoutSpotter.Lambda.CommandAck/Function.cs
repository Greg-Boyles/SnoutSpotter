using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.CommandAck;

public class Function
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public Function()
    {
        _dynamoDb = new AmazonDynamoDBClient();
        _tableName = Environment.GetEnvironmentVariable("COMMANDS_TABLE")
            ?? throw new InvalidOperationException("COMMANDS_TABLE not set");
    }

    public async Task FunctionHandler(JsonElement mqttPayload, ILambdaContext context)
    {
        try
        {
            if (!mqttPayload.TryGetProperty("command_id", out var idProp))
            {
                context.Logger.LogWarning("Ack payload missing command_id, skipping");
                return;
            }

            var commandId = idProp.GetString();
            if (string.IsNullOrEmpty(commandId))
                return;

            var status = mqttPayload.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var message = mqttPayload.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var error = mqttPayload.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
            var completedAt = mqttPayload.TryGetProperty("completed_at", out var c) ? c.GetString() ?? "" : "";

            var updateExpr = "SET #s = :status, completed_at = :completed_at";
            var exprValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new() { S = status },
                [":completed_at"] = new() { S = completedAt },
            };
            var exprNames = new Dictionary<string, string>
            {
                ["#s"] = "status"  // status is a reserved word in DynamoDB
            };

            if (!string.IsNullOrEmpty(message))
            {
                updateExpr += ", message = :message";
                exprValues[":message"] = new() { S = message };
            }

            if (!string.IsNullOrEmpty(error))
            {
                updateExpr += ", error = :error";
                exprValues[":error"] = new() { S = error };
            }

            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["command_id"] = new() { S = commandId }
                },
                UpdateExpression = updateExpr,
                ExpressionAttributeValues = exprValues,
                ExpressionAttributeNames = exprNames
            });

            context.Logger.LogInformation($"Updated command {commandId}: status={status}");
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Failed to process command ack: {ex.Message}");
            throw;
        }
    }
}
