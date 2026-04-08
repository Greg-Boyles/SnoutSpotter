using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using SnoutSpotter.Contracts;

namespace SnoutSpotter.TrainingAgent;

/// <summary>
/// Polls the SQS training job queue for work. Handles message lifecycle:
/// long-poll, visibility timeout extension, and completion/failure.
/// </summary>
public class SqsJobConsumer
{
    private readonly IAmazonSQS _sqs;
    private readonly string _queueUrl;
    private readonly ILogger _logger;

    public SqsJobConsumer(IAmazonSQS sqs, string queueUrl, ILogger logger)
    {
        _sqs = sqs;
        _queueUrl = queueUrl;
        _logger = logger;
    }

    /// <summary>
    /// Long-polls SQS for a training job message. Blocks up to 20 seconds.
    /// Returns null if no messages available.
    /// </summary>
    public async Task<(TrainingJobMessage Job, string ReceiptHandle)?> PollAsync(CancellationToken ct)
    {
        var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = _queueUrl,
            MaxNumberOfMessages = 1,
            WaitTimeSeconds = 20,
        }, ct);

        if (response.Messages.Count == 0)
            return null;

        var msg = response.Messages[0];
        var job = JsonSerializer.Deserialize<TrainingJobMessage>(msg.Body);
        if (job == null)
        {
            _logger.LogWarning("Failed to deserialize SQS message, deleting: {Id}", msg.MessageId);
            await DeleteAsync(msg.ReceiptHandle);
            return null;
        }

        _logger.LogInformation("Received training job from SQS: {JobId}", job.JobId);
        return (job, msg.ReceiptHandle);
    }

    /// <summary>Delete message after successful completion.</summary>
    public async Task DeleteAsync(string receiptHandle)
    {
        await _sqs.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = receiptHandle,
        });
    }

    /// <summary>
    /// Extend visibility timeout to prevent the message from becoming visible
    /// to other consumers while training is still running. Call every 10 minutes.
    /// </summary>
    public async Task ExtendVisibilityAsync(string receiptHandle, int seconds = 43200)
    {
        await _sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = _queueUrl,
            ReceiptHandle = receiptHandle,
            VisibilityTimeout = seconds,
        });
        _logger.LogInformation("Extended SQS visibility timeout by {Seconds}s", seconds);
    }

    /// <summary>
    /// Starts a background task that extends visibility every 10 minutes.
    /// Returns a CancellationTokenSource to stop it when training completes.
    /// </summary>
    public CancellationTokenSource StartVisibilityExtender(string receiptHandle)
    {
        var cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(10), cts.Token);
                try
                {
                    await ExtendVisibilityAsync(receiptHandle);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to extend visibility: {Error}", ex.Message);
                }
            }
        }, cts.Token);
        return cts;
    }
}
