using System.Net;
using System.Text.Json;
using Amazon.S3;
using SnoutSpotter.TrainingAgent;
using SnoutSpotter.TrainingAgent.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

const int ExitCodeUpdate = 42;

var logger = new ConsoleLogger();
logger.LogInformation("SnoutSpotter Training Agent starting...");

// Load config
var configPath = Environment.GetEnvironmentVariable("CONFIG_PATH") ?? "/app/config.yaml";
if (!File.Exists(configPath))
{
    logger.LogError("Config file not found: {Path}", configPath);
    return 1;
}

var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();
var config = deserializer.Deserialize<AgentConfig>(File.ReadAllText(configPath));

var thingName = config.IoT.ThingName;
logger.LogInformation("Thing name: {ThingName}", thingName);

// S3 client
var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(config.S3.Region));

// MQTT connection
await using var mqtt = new MqttManager(
    config.IoT.Endpoint,
    config.IoT.CertPath,
    config.IoT.KeyPath,
    config.IoT.RootCaPath,
    thingName,
    logger);

await mqtt.ConnectAsync();

// State
var jobRunner = new JobRunner(s3, config.S3.Bucket, mqtt, thingName, logger);
TrainingJobConfig? pendingJob = null;
string? pendingCancel = null;
string? pendingUpdate = null;
bool forceUpdate = false;
var cts = new CancellationTokenSource();
var jobCts = new CancellationTokenSource();
bool isTraining = false;

// Shadow delta handler
async Task OnShadowDelta(string topic, string payload)
{
    try
    {
        var doc = JsonDocument.Parse(payload);
        var state = doc.RootElement.GetProperty("state");

        if (state.TryGetProperty("trainingJob", out var jobElem))
        {
            var jobId = jobElem.GetProperty("jobId").GetString()!;
            var exportKey = jobElem.GetProperty("exportS3Key").GetString()!;
            var cfg = jobElem.TryGetProperty("config", out var cfgElem) ? cfgElem : default;

            pendingJob = new TrainingJobConfig(
                JobId: jobId,
                ExportS3Key: exportKey,
                Epochs: cfg.TryGetProperty("epochs", out var ep) ? ep.GetInt32() : 100,
                BatchSize: cfg.TryGetProperty("batch_size", out var bs) ? bs.GetInt32() : 16,
                ImageSize: cfg.TryGetProperty("image_size", out var ims) ? ims.GetInt32() : 640,
                LearningRate: cfg.TryGetProperty("learning_rate", out var lr) ? lr.GetDouble() : 0.01,
                Workers: cfg.TryGetProperty("workers", out var w) ? w.GetInt32() : 8,
                ModelBase: cfg.TryGetProperty("model_base", out var mb) ? mb.GetString()! : "yolov8n.pt",
                ResumeFrom: cfg.TryGetProperty("resume_from", out var rf) ? rf.GetString() : null);

            logger.LogInformation("Training job received: {JobId}", jobId);
        }

        if (state.TryGetProperty("cancelJob", out var cancelElem))
        {
            pendingCancel = cancelElem.GetString();
            logger.LogInformation("Cancel requested for: {JobId}", pendingCancel);
        }

        if (state.TryGetProperty("agentVersion", out var versionElem))
        {
            pendingUpdate = versionElem.GetString();
            forceUpdate = state.TryGetProperty("forceUpdate", out var fu) && fu.GetBoolean();
            logger.LogInformation("Agent update requested: v{Version} (force={Force})", pendingUpdate, forceUpdate);
        }
    }
    catch (Exception ex)
    {
        logger.LogError("Error processing shadow delta: {Error}", ex.Message);
    }
}

// Subscribe to shadow delta
var deltaTopic = $"$aws/things/{thingName}/shadow/update/delta";
await mqtt.SubscribeAsync(deltaTopic, OnShadowDelta);

// Request current shadow to catch pending deltas from while offline
var getAcceptedTopic = $"$aws/things/{thingName}/shadow/get/accepted";
await mqtt.SubscribeAsync(getAcceptedTopic, async (_, payload) =>
{
    try
    {
        var doc = JsonDocument.Parse(payload);
        if (doc.RootElement.TryGetProperty("state", out var state) &&
            state.TryGetProperty("delta", out var delta))
        {
            await OnShadowDelta("", JsonSerializer.Serialize(new { state = delta }));
        }
    }
    catch (Exception ex)
    {
        logger.LogError("Error processing shadow get: {Error}", ex.Message);
    }
});

await mqtt.PublishAsync($"$aws/things/{thingName}/shadow/get", "");
logger.LogInformation("Requested current shadow for pending deltas");

// Report initial shadow
async Task ReportShadow(string status = "idle", string? updateStatus = null, string? deferredVersion = null, string? deferReason = null)
{
    var gpu = GpuInfo.GetStatus();
    var reported = new Dictionary<string, object?>
    {
        ["agentType"] = "training-agent",
        ["agentVersion"] = "1.0.0",
        ["hostname"] = Dns.GetHostName(),
        ["lastHeartbeat"] = DateTime.UtcNow.ToString("O"),
        ["status"] = status,
        ["updateStatus"] = updateStatus ?? "idle",
    };

    if (gpu != null)
    {
        reported["gpu"] = new
        {
            name = gpu.Name,
            vramMb = gpu.VramMb,
            cudaVersion = gpu.CudaVersion,
            driverVersion = gpu.DriverVersion,
            temperatureC = gpu.TemperatureC,
            utilizationPercent = gpu.UtilizationPercent
        };
    }

    if (deferredVersion != null)
    {
        reported["deferredVersion"] = deferredVersion;
        reported["deferReason"] = deferReason;
    }

    var payload = JsonSerializer.Serialize(new { state = new { reported } });
    await mqtt.PublishAsync($"$aws/things/{thingName}/shadow/update", payload);
}

await ReportShadow();
logger.LogInformation("Agent started. Waiting for training jobs...");

// Main loop
var lastHeartbeat = DateTime.UtcNow;

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        // Heartbeat every 5 minutes
        if ((DateTime.UtcNow - lastHeartbeat).TotalMinutes >= 5)
        {
            await ReportShadow(isTraining ? "training" : "idle");
            lastHeartbeat = DateTime.UtcNow;
        }

        // Handle cancel
        if (pendingCancel != null)
        {
            jobRunner.Cancel();
            jobCts.Cancel();
            pendingCancel = null;
        }

        // Handle update
        if (pendingUpdate != null)
        {
            if (!isTraining || forceUpdate)
            {
                if (isTraining && forceUpdate)
                {
                    logger.LogWarning("Force update — cancelling training");
                    jobRunner.Cancel();
                    jobCts.Cancel();
                    await Task.Delay(2000);
                }

                logger.LogInformation("Applying update to v{Version}", pendingUpdate);
                await ReportShadow(updateStatus: "updating");
                Environment.Exit(ExitCodeUpdate);
            }
            else
            {
                logger.LogInformation("Update deferred — training in progress");
                await ReportShadow("training", "deferred", pendingUpdate,
                    $"Training job in progress");
                // Don't clear pendingUpdate — will apply after job finishes
            }
        }

        // Handle new job
        if (pendingJob != null && !isTraining)
        {
            var job = pendingJob;
            pendingJob = null;
            isTraining = true;
            jobCts = new CancellationTokenSource();

            await ReportShadow("training");

            try
            {
                await jobRunner.RunAsync(job, jobCts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Training job cancelled");
            }
            catch (Exception ex)
            {
                logger.LogError("Training job failed: {Error}", ex.Message);
            }

            isTraining = false;
            await ReportShadow("idle");

            // Apply deferred update now that job is done
            if (pendingUpdate != null)
            {
                logger.LogInformation("Applying deferred update to v{Version}", pendingUpdate);
                await ReportShadow(updateStatus: "updating");
                Environment.Exit(ExitCodeUpdate);
            }
        }

        await Task.Delay(2000, cts.Token);
    }
}
catch (OperationCanceledException) { }

logger.LogInformation("Agent shutting down");
return 0;
