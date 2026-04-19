using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services;

public class HouseholdIntegrationService : IHouseholdIntegrationService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public HouseholdIntegrationService(IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _tableName = config.Value.HouseholdsTable;
    }

    public async Task<SpcIntegrationState?> GetAsync(string householdId, CancellationToken ct = default)
    {
        var resp = await _dynamoDb.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            ct);
        if (!resp.IsItemSet) return null;
        if (!resp.Item.TryGetValue("spc_integration", out var attr) || attr.M == null || attr.M.Count == 0)
            return null;

        var m = attr.M;
        return new SpcIntegrationState(
            Status: m.GetValueOrDefault("status")?.S ?? "unknown",
            SpcHouseholdId: m.GetValueOrDefault("spc_household_id")?.S ?? "",
            SpcHouseholdName: m.GetValueOrDefault("spc_household_name")?.S ?? "",
            SpcUserEmail: m.GetValueOrDefault("spc_user_email")?.S ?? "",
            SecretArn: m.GetValueOrDefault("secret_arn")?.S ?? "",
            LinkedAt: m.GetValueOrDefault("linked_at")?.S ?? "",
            LastSyncAt: m.GetValueOrDefault("last_sync_at")?.S,
            LastError: m.GetValueOrDefault("last_error")?.S);
    }

    public async Task SaveAsync(string householdId, SpcIntegrationState state, CancellationToken ct = default)
    {
        var map = new Dictionary<string, AttributeValue>
        {
            ["status"] = new() { S = state.Status },
            ["spc_household_id"] = new() { S = state.SpcHouseholdId },
            ["spc_household_name"] = new() { S = state.SpcHouseholdName },
            ["spc_user_email"] = new() { S = state.SpcUserEmail },
            ["secret_arn"] = new() { S = state.SecretArn },
            ["linked_at"] = new() { S = state.LinkedAt }
        };
        if (!string.IsNullOrEmpty(state.LastSyncAt))
            map["last_sync_at"] = new AttributeValue { S = state.LastSyncAt };
        if (!string.IsNullOrEmpty(state.LastError))
            map["last_error"] = new AttributeValue { S = state.LastError };

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET spc_integration = :s",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":s"] = new() { M = map }
            }
        }, ct);
    }

    public async Task MarkTokenExpiredAsync(string householdId, CancellationToken ct = default)
    {
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET spc_integration.#status = :status, spc_integration.last_error = :err",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                ["#status"] = "status"
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new() { S = "token_expired" },
                [":err"] = new() { S = "auth_failed" }
            }
        }, ct);
    }

    public async Task ClearAsync(string householdId, CancellationToken ct = default)
    {
        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "REMOVE spc_integration"
        }, ct);
    }
}
