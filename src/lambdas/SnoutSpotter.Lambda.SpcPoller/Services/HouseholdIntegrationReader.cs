using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Read-side view of the spc_integration map on snout-spotter-households.
// The SPC Lambda owns writing; we only need a few fields and the ability to
// flip status -> token_expired when a 401 bubbles up mid-burst.
public class HouseholdIntegrationReader
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    public HouseholdIntegrationReader(IAmazonDynamoDB dynamo, string tableName)
    {
        _dynamo = dynamo;
        _tableName = tableName;
    }

    public async Task<(string? SpcHouseholdId, string? Status)> GetAsync(string householdId, CancellationToken ct)
    {
        var resp = await _dynamo.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            ct);
        if (!resp.IsItemSet) return (null, null);
        if (!resp.Item.TryGetValue("spc_integration", out var attr) || attr.M == null) return (null, null);
        var m = attr.M;
        return (
            m.GetValueOrDefault("spc_household_id")?.S,
            m.GetValueOrDefault("status")?.S);
    }

    public async Task MarkTokenExpiredAsync(string householdId, CancellationToken ct)
    {
        await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET spc_integration.#status = :status, spc_integration.last_error = :err",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new() { S = "token_expired" },
                [":err"] = new() { S = "auth_failed" }
            }
        }, ct);
    }

    public async Task SetLastSyncAtAsync(string householdId, DateTime at, CancellationToken ct)
    {
        await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET spc_integration.last_sync_at = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":t"] = new() { S = at.ToString("O") }
            }
        }, ct);
    }
}
