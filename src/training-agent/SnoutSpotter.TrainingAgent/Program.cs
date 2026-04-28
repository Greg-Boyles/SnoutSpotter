using System.Net;
using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.SQS;
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

var thingName = config.Iot.ThingName;
logger.LogInformation("Thing name: {ThingName}", thingName);

// AWS clients — use IoT Credentials Provider (same pattern as Pi)
var region = Amazon.RegionEndpoint.GetBySystemName(config.S3.Region);
var iotCreds = new IoTCredentialsProvider(
    config.CredentialsProvider.Endpoint,
    config.CredentialsProvider.RoleAlias,
    config.Iot.ThingName,
    config.Iot.CertPath,
    config.Iot.KeyPath,
    logger);
var s3 = new AmazonS3Client(iotCreds, region);
var sqs = new AmazonSQSClient(iotCreds, region);
var dynamoDb = new AmazonDynamoDBClient(iotCreds, region);

if (string.IsNullOrEmpty(config.S3.Bucket))
    throw new InvalidOperationException("S3 bucket not set in config — re-register to pick it up from the API");

var queueUrl = config.Training?.JobQueueUrl
    ?? Environment.GetEnvironmentVariable("TRAINING_JOB_QUEUE_URL")
    ?? throw new InvalidOperationException("Training job queue URL not configured");

var trainingJobsTable = config.Training?.JobsTable ?? "snout-spotter-training-jobs";

// MQTT connection (for progress reporting + shadow + cancel)
await using var mqtt = new MqttManager(
    config.Iot.Endpoint,
    config.Iot.CertPath,
    config.Iot.KeyPath,
    config.Iot.RootCaPath,
    thingName,
    logger);

await mqtt.ConnectAsync();

// State
var modelsTable = config.Training?.ModelsTable ?? "snout-spotter-models";
var jobRunner = new JobRunner(s3, dynamoDb, config.S3.Bucket, modelsTable, mqtt, thingName, logger);
var sqsConsumer = new SqsJobConsumer(sqs, queueUrl, logger);
string? pendingCancel = null;
string? pendingUpdate = null;
var forceUpdate = false;
var cts = new CancellationTokenSource();
var jobCts = new CancellationTokenSource();
var isTraining = false;
string? currentJobId = null;

// Shadow delta handler — only for cancel + update commands (NOT job dispatch)
async Task OnShadowDelta(string topic, string payload)
{
    try
    {
        var delta = JsonSerializer.Deserialize<ShadowDeltaMessage<AgentDesiredState>>(payload);
        if (delta?.State == null) return;
        var state = delta.State;

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

var deltaTopic = $"$aws/things/{thingName}/shadow/update/delta";
await mqtt.SubscribeAsync(deltaTopic, OnShadowDelta);

await mqtt.PublishAsync($"$aws/things/{thingName}/shadow/get", "");
logger.LogInformation("Requested current shadow for pending deltas");

// Report shadow
async Task ReportShadow(string status = "idle", string? jobId = null,
    TrainingProgress? jobProgress = null,
    string? updateStatus = null, string? deferredVersion = null, string? deferReason = null)
{
    var reported = new AgentReportedState
    {
        AgentVersion        = Environment.GetEnvironmentVariable("AGENT_VERSION") ?? "dev",
        MlScriptVersion     = jobRunner.CachedMlScriptVersion,
        Hostname            = Dns.GetHostName(),
        LastHeartbeat       = DateTime.UtcNow.ToString("O"),
        Status              = status,
        UpdateStatus        = updateStatus ?? "idle",
        Gpu                 = GpuInfo.GetStatus(),
        CurrentJobId        = jobId,
        CurrentJobProgress  = jobProgress,
        DeferredVersion     = deferredVersion,
        DeferReason         = deferReason
    };
    var payload = JsonSerializer.Serialize(ShadowReportedUpdate<AgentReportedState>.From(reported));
    await mqtt.PublishAsync($"$aws/things/{thingName}/shadow/update", payload);
}

await ReportShadow();
logger.LogInformation("Agent started. Polling SQS for training jobs...");

var lastHeartbeat = DateTime.UtcNow;

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        // Heartbeat every minute
        if ((DateTime.UtcNow - lastHeartbeat).TotalMinutes >= 1)
        {
            await ReportShadow(isTraining ? "training" : "idle", currentJobId, jobRunner.LatestProgress);
            lastHeartbeat = DateTime.UtcNow;
        }

        // Handle cancel (from shadow)
        if (pendingCancel != null)
        {
            jobRunner.Cancel();
            jobCts.Cancel();
            pendingCancel = null;
        }

        // Handle update (from shadow)
        if (pendingUpdate != null)
        {
            var currentVersion = Environment.GetEnvironmentVariable("AGENT_VERSION") ?? "dev";
            if (string.Equals(pendingUpdate, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation("Already running v{Version} — clearing shadow delta", currentVersion);
                await ReportShadow();
                pendingUpdate = null;
            }
            else if (!isTraining || forceUpdate)
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
                Directory.CreateDirectory("/app/host-state");
                File.WriteAllText("/app/host-state/pending-version", pendingUpdate);
                Environment.Exit(ExitCodeUpdate);
            }
            else
            {
                logger.LogInformation("Update deferred — training in progress");
                await ReportShadow("training", currentJobId, updateStatus: "deferred",
                    deferredVersion: pendingUpdate, deferReason: "Training job in progress");
            }
        }

        // Poll SQS for a job (if not already training)
        if (!isTraining)
        {
            var result = await sqsConsumer.PollAsync(cts.Token);
            if (result is var (job, receiptHandle))
            {
                isTraining = true;
                currentJobId = job.JobId;
                jobCts = new CancellationTokenSource();

                // Self-assign in DynamoDB
                try
                {
                    await dynamoDb.UpdateItemAsync(new UpdateItemRequest
                    {
                        TableName = trainingJobsTable,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["job_id"] = new() { S = job.JobId }
                        },
                        UpdateExpression = "SET agent_thing_name = :agent",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":agent"] = new() { S = thingName }
                        }
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Failed to self-assign job: {Error}", ex.Message);
                }

                await ReportShadow("training", job.JobId);

                // Start visibility extender (extends every 10 min during training)
                var visibilityCts = sqsConsumer.StartVisibilityExtender(receiptHandle);

                try
                {
                    await jobRunner.RunAsync(job, jobCts.Token);
                    // Success — delete message from queue
                    await sqsConsumer.DeleteAsync(receiptHandle);
                    logger.LogInformation("Job {JobId} completed, SQS message deleted", job.JobId);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Training job cancelled — message returns to queue");
                }
                catch (Exception ex)
                {
                    logger.LogError("Training job failed: {Error} — message returns to queue", ex.Message);
                }
                finally
                {
                    visibilityCts.Cancel();
                    visibilityCts.Dispose();
                }

                isTraining = false;
                currentJobId = null;
                jobRunner.LatestProgress = null;
                await ReportShadow("idle");

                // Apply deferred update
                if (pendingUpdate != null)
                {
                    var curVer = Environment.GetEnvironmentVariable("AGENT_VERSION") ?? "dev";
                    if (string.Equals(pendingUpdate, curVer, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogInformation("Already running v{Version} — clearing deferred update", curVer);
                        await ReportShadow();
                        pendingUpdate = null;
                    }
                    else
                    {
                        logger.LogInformation("Applying deferred update to v{Version}", pendingUpdate);
                        await ReportShadow(updateStatus: "updating");
                        Directory.CreateDirectory("/app/host-state");
                        File.WriteAllText("/app/host-state/pending-version", pendingUpdate);
                        Environment.Exit(ExitCodeUpdate);
                    }
                }
            }
        }
        else
        {
            await Task.Delay(2000, cts.Token);
        }
    }
}
catch (OperationCanceledException) { }

logger.LogInformation("Agent shutting down — reporting offline");
try { await ReportShadow("offline"); }
catch (Exception ex) { logger.LogWarning("Failed to report offline status on shutdown: {Error}", ex.Message); }
// mqtt is await using — DisconnectAsync called automatically on dispose

return 0;
