using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using SnoutSpotter.Lambda.SpcPoller.Models;

namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Thin wrapper around snout-spotter-spc-burst-state. One row per household.
public class BurstStateStore
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;
    private readonly TimeSpan _window;

    public BurstStateStore(IAmazonDynamoDB dynamo, string tableName, TimeSpan window)
    {
        _dynamo = dynamo;
        _tableName = tableName;
        _window = window;
    }

    public async Task<BurstState?> GetAsync(string householdId, CancellationToken ct)
    {
        var resp = await _dynamo.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            ct);
        if (!resp.IsItemSet) return null;

        DateTime pollUntil = default;
        if (resp.Item.TryGetValue("poll_until", out var pu) && !string.IsNullOrEmpty(pu.S))
            DateTime.TryParse(pu.S, null, System.Globalization.DateTimeStyles.RoundtripKind, out pollUntil);

        long? lastId = null;
        if (resp.Item.TryGetValue("last_timeline_id", out var lti) && lti.N != null && long.TryParse(lti.N, out var parsed))
            lastId = parsed;

        return new BurstState(
            HouseholdId: householdId,
            PollUntil: pollUntil,
            LastTimelineId: lastId,
            LastPollAt: resp.Item.TryGetValue("last_poll_at", out var lpa) ? lpa.S : null);
    }

    // Motion-triggered start/extend. New deadline = max(existing poll_until, now) + window.
    // Idempotent: multiple near-simultaneous motion events just extend to the same or later
    // deadline; they never reset a still-running burst backwards.
    public async Task<DateTime> StartOrExtendAsync(string householdId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var existing = await GetAsync(householdId, ct);
        var baseTime = existing != null && existing.PollUntil > now ? existing.PollUntil : now;
        var newDeadline = baseTime + _window;
        var ttlExpiry = new DateTimeOffset(newDeadline.AddHours(1)).ToUnixTimeSeconds();

        await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET poll_until = :pu, ttl_expiry = :ttl",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":pu"] = new() { S = newDeadline.ToString("O") },
                [":ttl"] = new() { N = ttlExpiry.ToString() }
            }
        }, ct);

        return newDeadline;
    }

    public async Task SetCursorAsync(string householdId, long lastTimelineId, CancellationToken ct)
    {
        await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET last_timeline_id = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":id"] = new() { N = lastTimelineId.ToString() }
            }
        }, ct);
    }

    public async Task SetLastPollAtAsync(string householdId, DateTime at, CancellationToken ct)
    {
        await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } },
            UpdateExpression = "SET last_poll_at = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":t"] = new() { S = at.ToString("O") }
            }
        }, ct);
    }
}
