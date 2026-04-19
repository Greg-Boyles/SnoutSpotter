using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Lambda.Spc.Models;
using SnoutSpotter.Lambda.Spc.Services.Interfaces;

namespace SnoutSpotter.Lambda.Spc.Services;

public class PetLinkService : IPetLinkService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public PetLinkService(IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _tableName = config.Value.PetsTable;
    }

    public async Task<List<string>> ListPetIdsAsync(string householdId, CancellationToken ct = default)
    {
        var ids = new List<string>();
        Dictionary<string, AttributeValue>? startKey = null;
        do
        {
            var resp = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "household_id = :hid",
                ProjectionExpression = "pet_id",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":hid"] = new() { S = householdId }
                },
                ExclusiveStartKey = startKey
            }, ct);
            foreach (var item in resp.Items)
            {
                if (item.TryGetValue("pet_id", out var pid) && !string.IsNullOrEmpty(pid.S))
                    ids.Add(pid.S);
            }
            startKey = resp.LastEvaluatedKey is { Count: > 0 } ? resp.LastEvaluatedKey : null;
        }
        while (startKey != null);
        return ids;
    }

    public async Task<int> ApplyMappingsAsync(string householdId, IEnumerable<PetMapping> mappings, CancellationToken ct = default)
    {
        var count = 0;
        foreach (var m in mappings)
        {
            await ApplyOneAsync(householdId, m, ct);
            count++;
        }
        return count;
    }

    public async Task ClearAllAsync(string householdId, CancellationToken ct = default)
    {
        var petIds = await ListPetIdsAsync(householdId, ct);
        foreach (var pid in petIds)
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = new Dictionary<string, AttributeValue>
                {
                    ["household_id"] = new() { S = householdId },
                    ["pet_id"] = new() { S = pid }
                },
                UpdateExpression = "REMOVE spc_pet_id, spc_pet_name"
            }, ct);
        }
    }

    private async Task ApplyOneAsync(string householdId, PetMapping m, CancellationToken ct)
    {
        var key = new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["pet_id"] = new() { S = m.PetId }
        };

        if (string.IsNullOrEmpty(m.SpcPetId))
        {
            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _tableName,
                Key = key,
                UpdateExpression = "REMOVE spc_pet_id, spc_pet_name",
                ConditionExpression = "attribute_exists(pet_id)"
            }, ct);
            return;
        }

        var setExpr = "SET spc_pet_id = :sid";
        var values = new Dictionary<string, AttributeValue>
        {
            [":sid"] = new() { S = m.SpcPetId }
        };
        if (!string.IsNullOrEmpty(m.SpcPetName))
        {
            setExpr += ", spc_pet_name = :sname";
            values[":sname"] = new() { S = m.SpcPetName };
        }
        else
        {
            setExpr += " REMOVE spc_pet_name";
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = key,
            UpdateExpression = setExpr,
            ExpressionAttributeValues = values,
            ConditionExpression = "attribute_exists(pet_id)"
        }, ct);
    }
}
