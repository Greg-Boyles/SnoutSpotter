using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.IoT;
using Amazon.IoT.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;
using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.Api.Services;

public class TrainingService : ITrainingService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonIoT _iot;
    private readonly AppConfig _config;

    public TrainingService(IAmazonDynamoDB dynamoDb, IAmazonIotData iotData, IAmazonIoT iot, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _iotData = iotData;
        _iot = iot;
        _config = config.Value;
    }

    public async Task<List<TrainerAgentSummary>> ListAgentsAsync()
    {
        var response = await _iot.ListThingsInThingGroupAsync(new ListThingsInThingGroupRequest
        {
            ThingGroupName = _config.TrainerThingGroup
        });

        var agents = new List<TrainerAgentSummary>();
        foreach (var thingName in response.Things)
        {
            try
            {
                var shadow = await GetShadowReportedAsync(thingName);
                var online = shadow?.LastHeartbeat != null &&
                    DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
                    (DateTime.UtcNow - lastHb).TotalMinutes < 5;

                agents.Add(new TrainerAgentSummary(
                    ThingName: thingName,
                    Online: online,
                    Version: shadow?.AgentVersion,
                    Hostname: shadow?.Hostname,
                    LastHeartbeat: shadow?.LastHeartbeat,
                    CurrentJobId: shadow?.CurrentJobId));
            }
            catch
            {
                agents.Add(new TrainerAgentSummary(thingName, false, null, null, null, null));
            }
        }

        return agents;
    }

    public async Task<object?> GetAgentStatusAsync(string thingName)
    {
        var shadow = await GetShadowReportedAsync(thingName);
        if (shadow == null) return null;

        var online = shadow.LastHeartbeat != null &&
            DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
            (DateTime.UtcNow - lastHb).TotalMinutes < 5;

        return new { thingName, online, reported = shadow };
    }

    public async Task TriggerAgentUpdateAsync(string thingName, string version)
    {
        var payload = JsonSerializer.Serialize(
            ShadowDesiredUpdate<AgentDesiredState>.From(
                new AgentDesiredState { AgentVersion = version }));

        await UpdateShadowAsync(thingName, payload);
    }

    public async Task<string> SubmitJobAsync(TrainingJobRequest request)
    {
        var jobId = $"tj-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..4]}";
        var now = DateTime.UtcNow.ToString("O");

        var jobParams = new TrainingJobParams
        {
            Epochs       = request.Epochs,
            BatchSize    = request.BatchSize,
            ImageSize    = request.ImageSize,
            LearningRate = request.LearningRate,
            Workers      = request.Workers,
            ModelBase    = request.ModelBase,
            ResumeFrom   = request.ResumeFrom
        };

        // 1. Write job to DynamoDB
        var item = new Dictionary<string, AttributeValue>
        {
            ["job_id"]       = new() { S = jobId },
            ["status"]       = new() { S = "pending" },
            ["export_id"]    = new() { S = request.ExportId },
            ["export_s3_key"]= new() { S = request.ExportS3Key },
            ["config"]       = new() { S = JsonSerializer.Serialize(jobParams) },
            ["created_at"]   = new() { S = now },
            ["notes"]        = new() { S = request.Notes ?? "" }
        };

        await _dynamoDb.PutItemAsync(_config.TrainingJobsTable, item);

        // 2. Queue to SQS — agents poll and self-assign
        if (string.IsNullOrEmpty(_config.TrainingJobQueueUrl))
            throw new InvalidOperationException("Training job queue not configured");

        using var sqsClient = new Amazon.SQS.AmazonSQSClient();
        var message = new Contracts.TrainingJobMessage(
            JobId: jobId,
            ExportS3Key: request.ExportS3Key,
            Config: new Contracts.TrainingJobParamsMessage(
                Epochs: request.Epochs,
                BatchSize: request.BatchSize,
                ImageSize: request.ImageSize,
                LearningRate: request.LearningRate,
                Workers: request.Workers,
                ModelBase: request.ModelBase,
                ResumeFrom: request.ResumeFrom));

        await sqsClient.SendMessageAsync(new Amazon.SQS.Model.SendMessageRequest
        {
            QueueUrl = _config.TrainingJobQueueUrl,
            MessageBody = JsonSerializer.Serialize(message)
        });

        return jobId;
    }

    public async Task<List<TrainingJobSummary>> ListJobsAsync(string? status = null, int limit = 50)
    {
        List<Dictionary<string, AttributeValue>> items;

        if (status != null)
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _config.TrainingJobsTable,
                IndexName = "by-status",
                KeyConditionExpression = "#s = :status",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":status"] = new() { S = status }
                },
                ScanIndexForward = false,
                Limit = limit
            });
            items = response.Items;
        }
        else
        {
            var response = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _config.TrainingJobsTable,
                Limit = limit
            });
            items = response.Items;
        }

        return items.Select(item =>
        {
            int? epochs = null;
            var configJson = item.GetValueOrDefault("config")?.S;
            if (configJson != null)
            {
                var cfg = JsonSerializer.Deserialize<TrainingJobParams>(configJson);
                epochs = cfg?.Epochs;
            }

            return new TrainingJobSummary(
                JobId: item.GetValueOrDefault("job_id")?.S ?? "",
                Status: item.GetValueOrDefault("status")?.S ?? "",
                AgentThingName: item.GetValueOrDefault("agent_thing_name")?.S,
                ExportId: item.GetValueOrDefault("export_id")?.S,
                Epochs: epochs,
                CreatedAt: item.GetValueOrDefault("created_at")?.S,
                CompletedAt: item.GetValueOrDefault("completed_at")?.S);
        })
        .OrderByDescending(j => j.CreatedAt)
        .ToList();
    }

    public async Task<TrainingJobDetail?> GetJobAsync(string jobId)
    {
        var response = await _dynamoDb.GetItemAsync(_config.TrainingJobsTable,
            new Dictionary<string, AttributeValue> { ["job_id"] = new() { S = jobId } });

        if (!response.IsItemSet) return null;
        var item = response.Item;

        return new TrainingJobDetail(
            JobId: item.GetValueOrDefault("job_id")?.S ?? "",
            Status: item.GetValueOrDefault("status")?.S ?? "",
            AgentThingName: item.GetValueOrDefault("agent_thing_name")?.S,
            ExportId: item.GetValueOrDefault("export_id")?.S,
            ExportS3Key: item.GetValueOrDefault("export_s3_key")?.S,
            Config: item.GetValueOrDefault("config")?.S,
            Progress: item.GetValueOrDefault("progress")?.S,
            Result: item.GetValueOrDefault("result")?.S,
            CheckpointS3Key: item.GetValueOrDefault("checkpoint_s3_key")?.S,
            Error: item.GetValueOrDefault("error")?.S,
            CreatedAt: item.GetValueOrDefault("created_at")?.S,
            StartedAt: item.GetValueOrDefault("started_at")?.S,
            CompletedAt: item.GetValueOrDefault("completed_at")?.S);
    }

    public async Task CancelJobAsync(string jobId)
    {
        var job = await GetJobAsync(jobId);
        if (job == null) throw new InvalidOperationException($"Job {jobId} not found");
        if (job.Status is "complete" or "failed" or "cancelled")
            throw new InvalidOperationException($"Job {jobId} is already {job.Status}");

        if (job.AgentThingName != null)
        {
            var payload = JsonSerializer.Serialize(
                ShadowDesiredUpdate<AgentDesiredState>.From(
                    new AgentDesiredState { CancelJob = jobId }));

            await UpdateShadowAsync(job.AgentThingName, payload);
        }

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _config.TrainingJobsTable,
            Key = new Dictionary<string, AttributeValue> { ["job_id"] = new() { S = jobId } },
            UpdateExpression = "SET #s = :status",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#s"] = "status" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":status"] = new() { S = "cancelling" }
            }
        });
    }

    private async Task<AgentReportedState?> GetShadowReportedAsync(string thingName)
    {
        try
        {
            var response = await _iotData.GetThingShadowAsync(new GetThingShadowRequest
            {
                ThingName = thingName
            });

            using var reader = new StreamReader(response.Payload);
            var json = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("state", out var state) &&
                state.TryGetProperty("reported", out var reported))
            {
                return JsonSerializer.Deserialize<AgentReportedState>(reported.GetRawText());
            }
        }
        catch { }

        return null;
    }

    private async Task UpdateShadowAsync(string thingName, string payload)
    {
        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
        });
    }
}
