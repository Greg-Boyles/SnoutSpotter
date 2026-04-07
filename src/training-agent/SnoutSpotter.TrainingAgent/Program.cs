using System.Net;
using System.Text.Json;
using Amazon.S3;
using SnoutSpotter.Shared.Training;
using SnoutSpotter.TrainingAgent;
using SnoutSpotter.TrainingAgent.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

const int ExitCodeUpdate = 42;

var logger = new ConsoleLogger();
logger.LogInformation("SnoutSpotter Training Agent starting...");

// First-run: self-register if no config exists
const string ConfigPath = "/app/state/config.yaml";
const string CertsDir = "/app/state/certs";

if (!File.Exists(ConfigPath))
{
    var agentName = Environment.GetEnvironmentVariable("AGENT_NAME");
    if (string.IsNullOrWhiteSpace(agentName))
    {
        logger.LogError("No config found at {Path} and AGENT_NAME env var is not set. " +
                        "Set AGENT_NAME to register this machine on first run.", ConfigPath);
        return 1;
    }

    var registrationUrl = Environment.GetEnvironmentVariable("TRAINER_REGISTRATION_URL");
    logger.LogInformation("No config found — registering as '{AgentName}'...", agentName);
    await RegistrationService.RegisterAsync(agentName, registrationUrl, CertsDir, ConfigPath);
    logger.LogInformation("Registration complete.");
}

var deserializer = new DeserializerBuilder()
    .WithNamingConvention(UnderscoredNamingConvention.Instance)
    .Build();

var config = deserializer.Deserialize<AgentConfig>(File.ReadAllText(ConfigPath));

var thingName = config.IoT.ThingName;
logger.LogInformation("Thing name: {ThingName}", thingName);

// S3 client
var s3 = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(config.S3.Region));

// Discover S3 bucket if not set in config
if (string.IsNullOrEmpty(config.S3.Bucket))
{
    var buckets = await s3.ListBucketsAsync();
    config.S3.Bucket = buckets.Buckets
        .FirstOrDefault(b => b.BucketName.StartsWith("snout-spotter-"))?.BucketName
        ?? throw new InvalidOperationException("Could not find snout-spotter-* S3 bucket");
    logger.LogInformation("Discovered S3 bucket: {Bucket}", config.S3.Bucket);
}

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
TrainingJobDesired? pendingJob = null;
string? pendingCancel = null;
string? pendingUpdate = null;
var forceUpdate = false;
var cts = new CancellationTokenSource();
var jobCts = new CancellationTokenSource();
var isTraining = false;

// Shadow delta handler
async Task OnShadowDelta(string topic, string payload)
{
    try
    {
        var delta = JsonSerializer.Deserialize<ShadowDeltaMessage<AgentDesiredState>>(payload);
        if (delta?.State == null) return;
        var state = delta.State;

        if (state.TrainingJob is { } job)
        {
            pendingJob = job;
            logger.LogInformation("Training job received: {JobId}", job.JobId);
        }

        if (state.CancelJob is { } cancelJobId)
        {
            pendingCancel = cancelJobId;
            logger.LogInformation("Cancel requested for: {JobId}", cancelJobId);
        }

        if (state.AgentVersion is { } version)
        {
            pendingUpdate = version;
            forceUpdate = state.ForceUpdate;
            logger.LogInformation("Agent update requested: v{Version} (force={Force})", version, forceUpdate);
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
    var reported = new AgentReportedState
    {
        AgentVersion    = "1.0.0",
        Hostname        = Dns.GetHostName(),
        LastHeartbeat   = DateTime.UtcNow.ToString("O"),
        Status          = status,
        UpdateStatus    = updateStatus ?? "idle",
        Gpu             = GpuInfo.GetStatus(),
        DeferredVersion = deferredVersion,
        DeferReason     = deferReason
    };
    var payload = JsonSerializer.Serialize(ShadowReportedUpdate<AgentReportedState>.From(reported));
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
