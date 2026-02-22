using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.S3.Notifications;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class IngestStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
    public required Table ClipsTable { get; init; }
}

public class IngestStack : Stack
{
    public IngestStack(Construct scope, string id, IngestStackProps props) : base(scope, id, props)
    {
        // FFmpeg Lambda layer (community-maintained)
        var ffmpegLayer = LayerVersion.FromLayerVersionArn(this, "FfmpegLayer",
            $"arn:aws:lambda:{Region}:017000801446:layer:AWSLambda-Python-FFmpeg:1");

        var ingestFunction = new Function(this, "IngestClipFunction", new FunctionProps
        {
            FunctionName = "snout-spotter-ingest-clip",
            Runtime = Runtime.DOTNET_8,
            Handler = "SnoutSpotter.Lambda.IngestClip::SnoutSpotter.Lambda.IngestClip.Function::FunctionHandler",
            Code = Code.FromAsset("../lambdas/SnoutSpotter.Lambda.IngestClip/bin/Release/net8.0/publish"),
            MemorySize = 1024,
            Timeout = Duration.Minutes(5),
            Layers = new[] { ffmpegLayer },
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["TABLE_NAME"] = props.ClipsTable.TableName
            }
        });

        // Grant permissions
        props.DataBucket.GrantReadWrite(ingestFunction);
        props.ClipsTable.GrantWriteData(ingestFunction);

        // S3 trigger: invoke Lambda on new .mp4 files in raw-clips/
        props.DataBucket.AddEventNotification(
            EventType.OBJECT_CREATED,
            new LambdaDestination(ingestFunction),
            new NotificationKeyFilter
            {
                Prefix = "raw-clips/",
                Suffix = ".mp4"
            });
    }
}
