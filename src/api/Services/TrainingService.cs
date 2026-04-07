using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.IoT;
using Amazon.IoT.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;

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
                var online = shadow?.TryGetProperty("lastHeartbeat", out var hb) == true &&
                    DateTime.TryParse(hb.GetString(), out var lastHb) &&
                    (DateTime.UtcNow - lastHb).TotalMinutes < 5;

                agents.Add(new TrainerAgentSummary(
                    ThingName: thingName,
                    Online: online,
                    Version: shadow?.TryGetProperty("agentVersion", out var v) == true ? v.GetString() : null,
                    Hostname: shadow?.TryGetProperty("hostname", out var h) == true ? h.GetString() : null,
                    LastHeartbeat: shadow?.TryGetProperty("lastHeartbeat", out var lb) == true ? lb.GetString() : null
                ));
            }
            catch
            {
                agents.Add(new TrainerAgentSummary(thingName, false, null, null, null));
            }
        }

        return agents;
    }

    public async Task<object?> GetAgentStatusAsync(string thingName)
    {
        var shadow = await GetShadowReportedAsync(thingName);
        if (shadow == null) return null;

        var online = shadow.Value.TryGetProperty("lastHeartbeat", out var hb) &&
            DateTime.TryParse(hb.GetString(), out var lastHb) &&
            (DateTime.UtcNow - lastHb).TotalMinutes < 5;

        return new
        {
            thingName,
            online,
            reported = shadow
        };
    }

    public async Task TriggerAgentUpdateAsync(string thingName, string version)
    {
        var payload = JsonSerializer.Serialize(new
        {
            state = new { desired = new { agentVersion = version } }
        });

        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
        });
    }

    public async Task<string> SubmitJobAsync(TrainingJobRequest request)
    {
        var jobId = $"tj-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString()[..4]}";
        var now = DateTime.UtcNow.ToString("O");

        var config = JsonSerializer.Serialize(new
        {
            epochs = request.Epochs,
            batch_size = request.BatchSize,
            image_size = request.ImageSize,
            learning_rate = request.LearningRate,
            workers = request.Workers,
            model_base = request.ModelBase,
            resume_from = request.ResumeFrom,
            notes = request.Notes
        });

        var item = new Dictionary<string, AttributeValue>
        {
            ["job_id"] = new() { S = jobId },
            ["status"] = new() { S = "pending" },
            ["export_id"] = new() { S = request.ExportId },
            ["export_s3_key"] = new() { S = request.ExportS3Key },
            ["config"] = new() { S = config },
            ["created_at"] = new() { S = now }
        };

        await _dynamoDb.PutItemAsync(_config.TrainingJobsTable, item);

        // Find an idle agent and write the job to its shadow
        var agents = await ListAgentsAsync();
        var idleAgent = agents.FirstOrDefault(a => a.Online);

        if (idleAgent != null)
        {
            var shadowPayload = JsonSerializer.Serialize(new
            {
                state = new
                {
                    desired = new
                    {
                        trainingJob = new
                        {
                            jobId,
                            exportS3Key = request.ExportS3Key,
                            config = new
                            {
                                epochs = request.Epochs,
                                batch_size = request.BatchSize,
                                image_size = request.ImageSize,
                                learning_rate = request.LearningRate,
                                workers = request.Workers,
                                model_base = request.ModelBase,
                                resume_from = request.ResumeFrom
                            }
                        }
                    }
                }
            });

            await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
            {
                ThingName = idleAgent.ThingName,
                Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(shadowPayload))
            });

            await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = _config.TrainingJobsTable,
                Key = new Dictionary<string, AttributeValue> { ["job_id"] = new() { S = jobId } },
                UpdateExpression = "SET agent_thing_name = :agent",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":agent"] = new() { S = idleAgent.ThingName }
                }
            });
        }

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

        return items.Select(item => new TrainingJobSummary(
            JobId: item.GetValueOrDefault("job_id")?.S ?? "",
            Status: item.GetValueOrDefault("status")?.S ?? "",
            AgentThingName: item.GetValueOrDefault("agent_thing_name")?.S,
            ExportId: item.GetValueOrDefault("export_id")?.S,
            Epochs: int.TryParse(
                JsonDocument.Parse(item.GetValueOrDefault("config")?.S ?? "{}").RootElement
                    .TryGetProperty("epochs", out var ep) ? ep.GetRawText() : "", out var epochs) ? epochs : null,
            CreatedAt: item.GetValueOrDefault("created_at")?.S,
            CompletedAt: item.GetValueOrDefault("completed_at")?.S
        ))
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
            var payload = JsonSerializer.Serialize(new
            {
                state = new { desired = new { cancelJob = jobId } }
            });

            await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
            {
                ThingName = job.AgentThingName,
                Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
            });
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

    private async Task<JsonElement?> GetShadowReportedAsync(string thingName)
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
                return reported;
            }
        }
        catch { }

        return null;
    }
}
