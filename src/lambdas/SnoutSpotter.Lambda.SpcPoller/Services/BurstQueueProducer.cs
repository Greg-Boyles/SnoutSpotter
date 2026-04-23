using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using SnoutSpotter.Lambda.SpcPoller.Models;

namespace SnoutSpotter.Lambda.SpcPoller.Services;

// Self-schedule helper: enqueue a `continue` message for the same household
// with a 30-second delay. SQS's DelaySeconds on SendMessage lets us build a
// chain that dies naturally when the burst window closes — no EventBridge
// cron, zero cost when no motion is happening.
public class BurstQueueProducer
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private const int ContinueDelaySeconds = 30;
    private const int MinRemainingBeforeContinueSeconds = 30;

    public BurstQueueProducer(IAmazonSQS sqs, string queueUrl)
    {
        _sqs = sqs;
        _queueUrl = queueUrl;
    }

    public async Task MaybeScheduleContinueAsync(string householdId, DateTime pollUntil, CancellationToken ct)
    {
        var remaining = pollUntil - DateTime.UtcNow;
        if (remaining.TotalSeconds < MinRemainingBeforeContinueSeconds)
            return; // window about to close — let the chain die

        var body = JsonSerializer.Serialize(new BurstMessage(householdId, BurstMessageKinds.Continue));
        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = _queueUrl,
            MessageBody = body,
            DelaySeconds = ContinueDelaySeconds
        }, ct);
    }
}
