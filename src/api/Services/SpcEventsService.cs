using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class SpcEventsService : ISpcEventsService
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    public SpcEventsService(IAmazonDynamoDB dynamo, IOptions<AppConfig> config)
    {
        _dynamo = dynamo;
        _tableName = config.Value.SpcEventsTable;
    }

    public async Task<SpcEventsPage> ListForPetAsync(string householdId, string petId, int limit, string? nextPageKey)
    {
        // Query is household-scoped (PK), filters client-side on pet_id.
        // Per CLAUDE.md: DynamoDB `Limit` applies before `FilterExpression`, so
        // we page with a larger inner Limit and accumulate matches until we hit
        // our caller's target or the scan is exhausted.
        var matched = new List<SpcEventDto>();
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (!string.IsNullOrEmpty(nextPageKey))
        {
            exclusiveStartKey = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["created_at_event"] = new() { S = nextPageKey }
            };
        }

        string? finalCursor = null;
        while (matched.Count < limit)
        {
            var resp = await _dynamo.QueryAsync(new QueryRequest
            {
                TableName = _tableName,
                KeyConditionExpression = "household_id = :hh",
                FilterExpression = "pet_id = :pid",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":hh"] = new() { S = householdId },
                    [":pid"] = new() { S = petId }
                },
                ScanIndexForward = false, // newest first
                Limit = 100,
                ExclusiveStartKey = exclusiveStartKey
            });

            foreach (var item in resp.Items)
            {
                if (matched.Count >= limit) break;
                matched.Add(ToDto(item));
                finalCursor = item["created_at_event"].S;
            }

            if (resp.LastEvaluatedKey is { Count: > 0 } && matched.Count < limit)
            {
                exclusiveStartKey = resp.LastEvaluatedKey;
            }
            else
            {
                // If we filled `limit` but DynamoDB still had more items to scan,
                // return the cursor of the last item we kept so the client can
                // pick up from there. If the query is fully exhausted, null.
                if (matched.Count >= limit && resp.LastEvaluatedKey is { Count: > 0 })
                {
                    // finalCursor already set to last matched item's SK
                }
                else
                {
                    finalCursor = null;
                }
                break;
            }
        }

        return new SpcEventsPage(matched, finalCursor);
    }

    private static SpcEventDto ToDto(Dictionary<string, AttributeValue> item)
    {
        int spcType = 0;
        if (item.TryGetValue("spc_event_type", out var st) && st.N != null && int.TryParse(st.N, out var parsed))
            spcType = parsed;

        int? weightChange = null;
        if (item.TryGetValue("weight_change", out var wc) && wc.N != null && int.TryParse(wc.N, out var wcp))
            weightChange = wcp;
        int? weightDuration = null;
        if (item.TryGetValue("weight_duration", out var wd) && wd.N != null && int.TryParse(wd.N, out var wdp))
            weightDuration = wdp;
        int? weightCurrent = null;
        if (item.TryGetValue("weight_current", out var wcur) && wcur.N != null && int.TryParse(wcur.N, out var wcurp))
            weightCurrent = wcurp;

        return new SpcEventDto(
            SpcEventId: item.GetValueOrDefault("spc_event_id")?.S ?? "",
            SpcEventType: spcType,
            EventCategory: item.GetValueOrDefault("event_category")?.S ?? "other",
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "",
            PetId: item.GetValueOrDefault("pet_id")?.S,
            SpcPetId: item.GetValueOrDefault("spc_pet_id")?.S,
            DeviceId: item.GetValueOrDefault("device_id")?.S,
            RawData: item.GetValueOrDefault("raw_data")?.S,
            WeightChange: weightChange,
            WeightDuration: weightDuration,
            WeightCurrent: weightCurrent);
    }
}
