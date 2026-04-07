using Amazon.CDK;
using SnoutSpotter.Infra.Stacks;

var app = new App();

// Tag all resources with Project tag
Tags.Of(app).Add("Project", "SnoutSpotter");

var env = new Amazon.CDK.Environment
{
    Account = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_ACCOUNT"),
    Region = System.Environment.GetEnvironmentVariable("CDK_DEFAULT_REGION") ?? "eu-west-1"
};

var coreStack = new CoreStack(app, "SnoutSpotter-Core", new StackProps { Env = env });

var ingestStack = new IngestStack(app, "SnoutSpotter-Ingest", new IngestStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket,
    ClipsTable = coreStack.ClipsTable,
    IngestEcrRepo = coreStack.IngestEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest"
});

var inferenceStack = new InferenceStack(app, "SnoutSpotter-Inference", new InferenceStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket,
    ClipsTable = coreStack.ClipsTable,
    InferenceEcrRepo = coreStack.InferenceEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest"
});

var oktaIssuer = (string?)app.Node.TryGetContext("oktaIssuer") ?? "https://integrator-4203185.okta.com/oauth2/default";
var allowedOrigin = (string?)app.Node.TryGetContext("allowedOrigin") ?? "https://d2c95zo6ucmtrt.cloudfront.net";

var iotStack = new IoTStack(app, "SnoutSpotter-IoT", new IoTStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket
});

var logIngestionStack = new LogIngestionStack(app, "SnoutSpotter-LogIngestion", new LogIngestionStackProps
{
    Env = env,
    LogIngestionEcrRepo = coreStack.LogIngestionEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest",
    PiLogGroupName = iotStack.PiLogGroupName
});

var commandAckStack = new CommandAckStack(app, "SnoutSpotter-CommandAck", new CommandAckStackProps
{
    Env = env,
    CommandAckEcrRepo = coreStack.CommandAckEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest",
    CommandsTable = coreStack.CommandsTable
});

var piMgmtStack = new PiMgmtStack(app, "SnoutSpotter-PiMgmt", new PiMgmtStackProps
{
    Env = env,
    PiMgmtEcrRepo = coreStack.PiMgmtEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest",
    IoTThingGroupName = iotStack.ThingGroupName,
    IoTPolicyName = iotStack.PolicyName,
    TrainerThingGroupName = iotStack.TrainerThingGroupName,
    TrainerPolicyName = iotStack.TrainerPolicyName
});

var autoLabelStack = new AutoLabelStack(app, "SnoutSpotter-AutoLabel", new AutoLabelStackProps
{
    Env = env,
    AutoLabelEcrRepo = coreStack.AutoLabelEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest",
    DataBucket = coreStack.DataBucket,
    LabelsTable = coreStack.LabelsTable
});

var apiStack = new ApiStack(app, "SnoutSpotter-Api", new ApiStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket,
    ClipsTable = coreStack.ClipsTable,
    CommandsTable = coreStack.CommandsTable,
    LabelsTable = coreStack.LabelsTable,
    ExportsTable = coreStack.ExportsTable,
    ApiEcrRepo = coreStack.ApiEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest",
    OktaIssuer = oktaIssuer,
    AllowedOrigin = allowedOrigin,
    BackfillQueueUrl = autoLabelStack.BackfillQueue.QueueUrl
});

var exportDatasetStack = new ExportDatasetStack(app, "SnoutSpotter-ExportDataset", new ExportDatasetStackProps
{
    Env = env,
    ExportDatasetEcrRepo = coreStack.ExportDatasetEcrRepo,
    ImageTag = System.Environment.GetEnvironmentVariable("IMAGE_TAG") ?? "latest",
    DataBucket = coreStack.DataBucket,
    LabelsTable = coreStack.LabelsTable,
    ExportsTable = coreStack.ExportsTable
});

var webStack = new WebStack(app, "SnoutSpotter-Web", new StackProps { Env = env });

var monitoringStack = new MonitoringStack(app, "SnoutSpotter-Monitoring", new MonitoringStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket
});

var cicdStack = new CiCdStack(app, "SnoutSpotter-CiCd", new StackProps { Env = env });

app.Synth();
