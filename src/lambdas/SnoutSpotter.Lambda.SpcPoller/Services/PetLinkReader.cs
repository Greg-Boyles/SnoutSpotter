using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Maps spc_pet_id -> our internal pet-... id for the household. Built once
// per poll by Querying snout-spotter-pets and filtering to rows that have a
// non-null spc_pet_id attribute.
public class PetLinkReader
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    public PetLinkReader(IAmazonDynamoDB dynamo, string tableName)
    {
        _dynamo = dynamo;
        _tableName = tableName;
    }

    public async Task<Dictionary<string, string>> GetSpcToInternalMapAsync(string householdId, CancellationToken ct)
    {
        // spc_pet_id now lives inside a nested spc_integration map — same
        // shape as household and device rows. DynamoDB ProjectionExpression
        // works at attribute granularity so we project the whole map and
        // pull the one field we care about in memory.
        var map = new Dictionary<string, string>();
        Dictionary<string, AttributeValue>? start = null;
        do
        {
            var resp = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "household_id = :hh",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":hh"] = new() { S = householdId }
                },
                ProjectionExpression = "pet_id, spc_integration",
                ExclusiveStartKey = start
            }, ct);
            foreach (var item in resp.Items)
            {
                if (!item.TryGetValue("pet_id", out var petId) || string.IsNullOrEmpty(petId.S))
                    continue;
                if (!item.TryGetValue("spc_integration", out var integration) || integration.M == null)
                    continue;
                if (integration.M.TryGetValue("spc_pet_id", out var spcId) && !string.IsNullOrEmpty(spcId.S))
                {
                    map[spcId.S] = petId.S;
                }
            }
            start = resp.LastEvaluatedKey is { Count: > 0 } ? resp.LastEvaluatedKey : null;
        }
        while (start != null);
        return map;
    }
}
