using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.ECR;
using Amazon.ECR.Model;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;
using SnoutSpotter.Shared.Training;

namespace SnoutSpotter.Api.Services;

public partial class TrainingService : ITrainingService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonIoT _iot;
    private readonly IAmazonECR _ecr;
    private readonly IAmazonS3 _s3;
    private readonly AppConfig _config;

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
    private string? _cachedLatestVersion;
    private DateTime _cacheExpiry;

    [GeneratedRegex(@"^v\d+\.\d+\.\d+$")]
    private static partial Regex VersionTagRegex();

    public TrainingService(IAmazonDynamoDB dynamoDb, IAmazonIotData iotData, IAmazonIoT iot,
        IAmazonECR ecr, IAmazonS3 s3, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _iotData = iotData;
        _iot = iot;
        _ecr = ecr;
        _s3 = s3;
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
                    (DateTime.UtcNow - lastHb).TotalMinutes < 2;

                var gpu = shadow?.Gpu == null ? null : new TrainerGpuSummary(
                    shadow.Gpu.Name,
                    shadow.Gpu.VramMb,
                    shadow.Gpu.TemperatureC,
                    shadow.Gpu.UtilizationPercent);

                var progress = shadow?.CurrentJobProgress == null ? null : new TrainerProgressSummary(
                    shadow.CurrentJobProgress.Epoch,
                    shadow.CurrentJobProgress.TotalEpochs,
                    shadow.CurrentJobProgress.MAP50);

                agents.Add(new TrainerAgentSummary(
                    ThingName: thingName,
                    Online: online,
                    Version: shadow?.AgentVersion,
                    MlScriptVersion: shadow?.MlScriptVersion,
                    Hostname: shadow?.Hostname,
                    LastHeartbeat: shadow?.LastHeartbeat,
                    CurrentJobId: shadow?.CurrentJobId,
                    Status: shadow?.Status,
                    Gpu: gpu,
                    CurrentJobProgress: progress));
            }
            catch
            {
                agents.Add(new TrainerAgentSummary(thingName, false, null, null, null, null, null, null, null, null));
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
            (DateTime.UtcNow - lastHb).TotalMinutes < 2;

        return new { thingName, online, reported = shadow };
    }

    public async Task TriggerAgentUpdateAsync(string thingName, string version)
    {
        var payload = JsonSerializer.Serialize(
            ShadowDesiredUpdate<AgentDesiredState>.From(
                new AgentDesiredState { AgentVersion = version }));

        await UpdateShadowAsync(thingName, payload);
    }

    public async Task<string?> GetLatestAgentVersionAsync()
    {
        if (_cachedLatestVersion != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedLatestVersion;

        try
        {
            var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _config.BucketName,
                Key = "releases/training-agent/manifest.json"
            });

            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync();
            var doc = JsonDocument.Parse(json);

            _cachedLatestVersion = doc.RootElement.GetProperty("version").GetString();
            _cacheExpiry = DateTime.UtcNow.Add(CacheDuration);
            return _cachedLatestVersion;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<TrainingAgentRelease>> ListAgentReleasesAsync()
    {
        var latest = await GetLatestAgentVersionAsync();
        var releases = new List<TrainingAgentRelease>();

        var response = await _ecr.DescribeImagesAsync(new DescribeImagesRequest
        {
            RepositoryName = "snout-spotter-training-agent"
        });

        foreach (var image in response.ImageDetails)
        {
            var versionTag = image.ImageTags?.FirstOrDefault(t => VersionTagRegex().IsMatch(t));
            if (versionTag == null) continue;

            var version = versionTag[1..]; // strip leading 'v'
            releases.Add(new TrainingAgentRelease(
                version,
                image.ImagePushedAt.ToString("O"),
                version == latest));
        }

        return releases.OrderByDescending(r => r.ImagePushedAt).ToList();
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
            ["config"]       = new() { M = ToMap(jobParams) },
            ["created_at"]   = new() { S = now },
            ["notes"]        = new() { S = request.Notes ?? "" },
            ["job_type"]     = new() { S = request.JobType }
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
                ResumeFrom: request.ResumeFrom),
            JobType: request.JobType);

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
            var configMap = item.GetValueOrDefault("config")?.M;
            int? epochs = configMap != null && configMap.TryGetValue("epochs", out var ep)
                ? int.Parse(ep.N) : null;

            var resultMap = item.GetValueOrDefault("result")?.M;
            double? finalMAP50 = resultMap != null && resultMap.TryGetValue("final_mAP50", out var fm)
                ? double.Parse(fm.N, CultureInfo.InvariantCulture) : null;

            return new TrainingJobSummary(
                JobId: item.GetValueOrDefault("job_id")?.S ?? "",
                Status: item.GetValueOrDefault("status")?.S ?? "",
                AgentThingName: item.GetValueOrDefault("agent_thing_name")?.S,
                ExportId: item.GetValueOrDefault("export_id")?.S,
                Epochs: epochs,
                CreatedAt: item.GetValueOrDefault("created_at")?.S,
                StartedAt: item.GetValueOrDefault("started_at")?.S,
                CompletedAt: item.GetValueOrDefault("completed_at")?.S,
                FinalMAP50: finalMAP50,
                JobType: item.GetValueOrDefault("job_type")?.S ?? "detector");
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

        var configMap = item.GetValueOrDefault("config")?.M;
        var progressMap = item.GetValueOrDefault("progress")?.M;
        var resultMap = item.GetValueOrDefault("result")?.M;

        return new TrainingJobDetail(
            JobId: item.GetValueOrDefault("job_id")?.S ?? "",
            Status: item.GetValueOrDefault("status")?.S ?? "",
            AgentThingName: item.GetValueOrDefault("agent_thing_name")?.S,
            ExportId: item.GetValueOrDefault("export_id")?.S,
            ExportS3Key: item.GetValueOrDefault("export_s3_key")?.S,
            Config:   configMap   != null ? FromConfigMap(configMap)     : null,
            Progress: progressMap != null ? FromProgressMap(progressMap) : null,
            Result:   resultMap   != null ? FromResultMap(resultMap)     : null,
            CheckpointS3Key: item.GetValueOrDefault("checkpoint_s3_key")?.S,
            Error: item.GetValueOrDefault("error")?.S,
            FailedStage: item.GetValueOrDefault("failed_stage")?.S,
            CreatedAt: item.GetValueOrDefault("created_at")?.S,
            StartedAt: item.GetValueOrDefault("started_at")?.S,
            CompletedAt: item.GetValueOrDefault("completed_at")?.S,
            JobType: item.GetValueOrDefault("job_type")?.S ?? "detector");
    }

    public async Task DeleteJobAsync(string jobId)
    {
        var job = await GetJobAsync(jobId);
        if (job == null) throw new InvalidOperationException($"Job {jobId} not found");
        if (job.Status is not ("complete" or "failed" or "cancelled" or "cancelling"))
            throw new InvalidOperationException($"Job {jobId} cannot be deleted while in '{job.Status}' state");

        await _dynamoDb.DeleteItemAsync(_config.TrainingJobsTable,
            new Dictionary<string, AttributeValue> { ["job_id"] = new() { S = jobId } });
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

    // ── DynamoDB Map helpers ────────────────────────────────────────────────

    private static Dictionary<string, AttributeValue> ToMap(TrainingJobParams p)
    {
        var m = new Dictionary<string, AttributeValue>
        {
            ["epochs"]        = new() { N = p.Epochs.ToString() },
            ["batch_size"]    = new() { N = p.BatchSize.ToString() },
            ["image_size"]    = new() { N = p.ImageSize.ToString() },
            ["learning_rate"] = new() { N = p.LearningRate.ToString("G", CultureInfo.InvariantCulture) },
            ["workers"]       = new() { N = p.Workers.ToString() },
            ["model_base"]    = new() { S = p.ModelBase },
        };
        if (p.ResumeFrom != null) m["resume_from"] = new() { S = p.ResumeFrom };
        return m;
    }

    private static TrainingJobParams FromConfigMap(Dictionary<string, AttributeValue> m) => new()
    {
        Epochs       = int.Parse(m["epochs"].N),
        BatchSize    = int.Parse(m["batch_size"].N),
        ImageSize    = int.Parse(m["image_size"].N),
        LearningRate = double.Parse(m["learning_rate"].N, CultureInfo.InvariantCulture),
        Workers      = int.Parse(m["workers"].N),
        ModelBase    = m["model_base"].S,
        ResumeFrom   = m.TryGetValue("resume_from", out var rf) ? rf.S : null,
    };

    private static TrainingProgress FromProgressMap(Dictionary<string, AttributeValue> m) => new()
    {
        Epoch             = int.Parse(m["epoch"].N),
        TotalEpochs       = int.Parse(m["total_epochs"].N),
        TrainLoss         = m.TryGetValue("train_loss", out var tl)  ? double.Parse(tl.N,  CultureInfo.InvariantCulture) : null,
        ValLoss           = m.TryGetValue("val_loss", out var vl)    ? double.Parse(vl.N,  CultureInfo.InvariantCulture) : null,
        MAP50             = m.TryGetValue("mAP50", out var mp)       ? double.Parse(mp.N,  CultureInfo.InvariantCulture) : null,
        MAP50_95          = m.TryGetValue("mAP50_95", out var mp95)  ? double.Parse(mp95.N, CultureInfo.InvariantCulture) : null,
        BestMAP50         = m.TryGetValue("best_mAP50", out var bm)  ? double.Parse(bm.N,  CultureInfo.InvariantCulture) : null,
        ElapsedSeconds    = m.TryGetValue("elapsed_seconds", out var es)  ? long.Parse(es.N)  : null,
        EtaSeconds        = m.TryGetValue("eta_seconds", out var eta)     ? long.Parse(eta.N) : null,
        GpuUtilPercent    = m.TryGetValue("gpu_util_percent", out var gu) ? int.Parse(gu.N)   : null,
        GpuTempC          = m.TryGetValue("gpu_temp_c", out var gt)       ? int.Parse(gt.N)   : null,
        EpochProgress     = m.TryGetValue("epoch_progress", out var ep)    ? int.Parse(ep.N)   : null,
        DownloadBytes     = m.TryGetValue("download_bytes", out var db)   ? long.Parse(db.N)  : null,
        DownloadTotalBytes = m.TryGetValue("download_total_bytes", out var dtb) ? long.Parse(dtb.N) : null,
        DownloadSpeedMbps = m.TryGetValue("download_speed_mbps", out var ds) ? double.Parse(ds.N, CultureInfo.InvariantCulture) : null,
        Accuracy          = m.TryGetValue("accuracy", out var ac)  ? double.Parse(ac.N, CultureInfo.InvariantCulture) : null,
        F1Score           = m.TryGetValue("f1_score", out var f1)  ? double.Parse(f1.N, CultureInfo.InvariantCulture) : null,
    };

    private static TrainingResult FromResultMap(Dictionary<string, AttributeValue> m) => new()
    {
        ModelS3Key          = m["model_s3_key"].S,
        ModelSizeMb         = double.Parse(m["model_size_mb"].N, CultureInfo.InvariantCulture),
        FinalMAP50          = m.TryGetValue("final_mAP50", out var fmv) ? double.Parse(fmv.N, CultureInfo.InvariantCulture) : 0,
        TotalEpochs         = int.Parse(m["total_epochs"].N),
        BestEpoch           = int.Parse(m["best_epoch"].N),
        TrainingTimeSeconds = long.Parse(m["training_time_seconds"].N),
        DatasetImages       = int.Parse(m["dataset_images"].N),
        Classes             = m.TryGetValue("classes", out var cl) ? [.. cl.L.Select(x => x.S)] : [],
        FinalMAP50_95       = m.TryGetValue("final_mAP50_95", out var f95) ? double.Parse(f95.N, CultureInfo.InvariantCulture) : null,
        Precision           = m.TryGetValue("precision", out var pr) ? double.Parse(pr.N, CultureInfo.InvariantCulture) : null,
        Recall              = m.TryGetValue("recall", out var rc)    ? double.Parse(rc.N, CultureInfo.InvariantCulture) : null,
        Accuracy            = m.TryGetValue("accuracy", out var ac)  ? double.Parse(ac.N, CultureInfo.InvariantCulture) : null,
        F1Score             = m.TryGetValue("f1_score", out var f1)  ? double.Parse(f1.N, CultureInfo.InvariantCulture) : null,
    };

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
