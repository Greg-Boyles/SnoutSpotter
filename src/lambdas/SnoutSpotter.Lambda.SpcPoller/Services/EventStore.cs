using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using SnoutSpotter.Spc.Client.Models;

namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Writes rows into snout-spotter-spc-events. Natural PK+SK
// (household_id, {created_at}#{spc_event_id}) is idempotent under PutItem —
// re-delivery of the same SPC timeline id from SQS is safe.
public class EventStore
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    public EventStore(IAmazonDynamoDB dynamo, string tableName)
    {
        _dynamo = dynamo;
        _tableName = tableName;
    }

    public async Task WriteAsync(
        string householdId,
        SpcTimelineResource evt,
        Dictionary<string, string> spcToInternalPetMap,
        CancellationToken ct)
    {
        var createdAt = evt.CreatedAt ?? DateTime.UtcNow.ToString("O");
        var sk = $"{createdAt}#{evt.Id}";
        var spcPetId = evt.Pets?.FirstOrDefault()?.Id.ToString();
        var internalPetId = spcPetId != null && spcToInternalPetMap.TryGetValue(spcPetId, out var p) ? p : null;
        var deviceId = evt.Devices?.FirstOrDefault()?.Id.ToString();

        var item = new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["created_at_event"] = new() { S = sk },
            ["spc_event_id"] = new() { S = evt.Id.ToString() },
            ["spc_event_type"] = new() { N = evt.Type.ToString() },
            ["event_category"] = new() { S = EventCategorizer.Categorize(evt.Type) },
            ["created_at"] = new() { S = createdAt }
        };
        if (internalPetId != null) item["pet_id"] = new AttributeValue { S = internalPetId };
        if (spcPetId != null) item["spc_pet_id"] = new AttributeValue { S = spcPetId };
        if (deviceId != null) item["device_id"] = new AttributeValue { S = deviceId };
        if (!string.IsNullOrEmpty(evt.Data)) item["raw_data"] = new AttributeValue { S = evt.Data };

        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = item
        }, ct);
    }
}
