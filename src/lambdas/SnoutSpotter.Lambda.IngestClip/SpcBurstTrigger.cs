using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace SnoutSpotter.Lambda.IngestClip;

// Motion-triggered Sure Pet Care burst poller hook. When a clip lands for a
// household that has at least one SPC device link, send a `motion` message
// onto the SPC burst queue. The SpcPoller Lambda takes it from there.
//
// Kept as a single class to minimise the blast radius in IngestClip.Function —
// this is a side-effect that must never fail the clip write, so every path is
// wrapped and swallows errors with a log line.
public class SpcBurstTrigger
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly IAmazonSQS _sqs;
    private readonly string? _devicesTable;
    private readonly string? _burstQueueUrl;

    public SpcBurstTrigger(IAmazonDynamoDB dynamo, IAmazonSQS sqs, string? devicesTable, string? burstQueueUrl)
    {
        _dynamo = dynamo;
        _sqs = sqs;
        _devicesTable = devicesTable;
        _burstQueueUrl = burstQueueUrl;
    }

    public async Task MaybeFireAsync(string? householdId, ILambdaContext ctx)
    {
        if (string.IsNullOrEmpty(householdId)) return;
        if (string.IsNullOrEmpty(_devicesTable) || string.IsNullOrEmpty(_burstQueueUrl))
        {
            // Feature not configured on this Lambda yet — fine during rollout.
            return;
        }

        try
        {
            if (!await HasAnySpcLinkAsync(householdId))
                return;

            var body = JsonSerializer.Serialize(new { householdId, kind = "motion" });
            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = _burstQueueUrl,
                MessageBody = body
            });
        }
        catch (Exception ex)
        {
            // Never let burst-trigger side effects fail clip ingestion.
            ctx.Logger.LogWarning($"SPC burst trigger failed for {householdId}: {ex.Message}");
        }
    }

    // Cheap existence check — Query for link# rows, Limit=1, projection-only.
    private async Task<bool> HasAnySpcLinkAsync(string householdId)
    {
        var resp = await _dynamo.QueryAsync(new QueryRequest
        {
            TableName = _devicesTable,
            KeyConditionExpression = "household_id = :hh AND begins_with(sk, :p)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":hh"] = new() { S = householdId },
                [":p"] = new() { S = "link#spc#" }
            },
            ProjectionExpression = "sk",
            Limit = 1
        });
        return resp.Items.Count > 0;
    }
}
