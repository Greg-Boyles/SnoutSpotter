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
                ProjectionExpression = "pet_id, spc_pet_id",
                ExclusiveStartKey = start
            }, ct);
            foreach (var item in resp.Items)
            {
                if (item.TryGetValue("spc_pet_id", out var spcId) && !string.IsNullOrEmpty(spcId.S)
                    && item.TryGetValue("pet_id", out var petId) && !string.IsNullOrEmpty(petId.S))
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
