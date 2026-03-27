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

var apiStack = new ApiStack(app, "SnoutSpotter-Api", new ApiStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket,
    ClipsTable = coreStack.ClipsTable,
    ApiEcrRepo = coreStack.ApiEcrRepo
});

var webStack = new WebStack(app, "SnoutSpotter-Web", new StackProps { Env = env });

var monitoringStack = new MonitoringStack(app, "SnoutSpotter-Monitoring", new MonitoringStackProps
{
    Env = env,
    DataBucket = coreStack.DataBucket
});

var cicdStack = new CiCdStack(app, "SnoutSpotter-CiCd", new StackProps { Env = env });

app.Synth();
