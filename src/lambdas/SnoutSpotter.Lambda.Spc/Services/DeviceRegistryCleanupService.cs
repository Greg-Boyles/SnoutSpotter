using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Lambda.Spc.Services.Interfaces;

namespace SnoutSpotter.Lambda.Spc.Services;

public class DeviceRegistryCleanupService : IDeviceRegistryCleanupService
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;
    private readonly ILogger<DeviceRegistryCleanupService> _log;

    public DeviceRegistryCleanupService(IAmazonDynamoDB dynamo, IOptions<AppConfig> config, ILogger<DeviceRegistryCleanupService> log)
    {
        _dynamo = dynamo;
        _tableName = config.Value.DevicesTable;
        _log = log;
    }

    public async Task ClearSpcDevicesAndLinksAsync(string householdId, CancellationToken ct = default)
    {
        await SweepPrefixAsync(householdId, "spc#", ct);
        await SweepPrefixAsync(householdId, "link#spc#", ct);
    }

    private async Task SweepPrefixAsync(string householdId, string prefix, CancellationToken ct)
    {
        Dictionary<string, AttributeValue>? exclusive = null;
        do
        {
            var resp = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "household_id = :hh AND begins_with(sk, :p)",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":hh"] = new() { S = householdId },
                    [":p"] = new() { S = prefix }
                },
                ProjectionExpression = "household_id, sk",
                ExclusiveStartKey = exclusive
            }, ct);

            // BatchWriteItem takes up to 25 ops; chunk the page.
            foreach (var batch in resp.Items.Chunk(25))
            {
                var reqs = batch.Select(item => new WriteRequest
                {
                    DeleteRequest = new DeleteRequest
                    {
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["household_id"] = item["household_id"],
                            ["sk"] = item["sk"]
                        }
                    }
                }).ToList();

                await _dynamo.BatchWriteItemAsync(new BatchWriteItemRequest
                {
                    RequestItems = new Dictionary<string, List<WriteRequest>>
                    {
                        [_tableName] = reqs
                    }
                }, ct);
            }

            exclusive = resp.LastEvaluatedKey?.Count > 0 ? resp.LastEvaluatedKey : null;
        }
        while (exclusive != null);

        _log.LogInformation("Cleared device-registry rows with prefix {Prefix} for household {Household}", prefix, householdId);
    }
}
