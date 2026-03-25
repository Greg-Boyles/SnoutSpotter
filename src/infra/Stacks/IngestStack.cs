using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
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
        // Note: FFmpeg layer removed - keyframe extraction can be added later with custom layer
        var ingestFunction = new Function(this, "IngestClipFunction", new FunctionProps
        {
            FunctionName = "snout-spotter-ingest-clip",
            Runtime = Runtime.DOTNET_8,
            Handler = "SnoutSpotter.Lambda.IngestClip::SnoutSpotter.Lambda.IngestClip.Function::FunctionHandler",
            Code = Code.FromAsset("../lambdas/SnoutSpotter.Lambda.IngestClip/bin/Release/net8.0/publish"),
            MemorySize = 512,
            Timeout = Duration.Minutes(2),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["TABLE_NAME"] = props.ClipsTable.TableName
            }
        });

        // Grant permissions
        props.DataBucket.GrantReadWrite(ingestFunction);
        props.ClipsTable.GrantWriteData(ingestFunction);

        // Output the function ARN so we can manually configure S3 event notification
        _ = new CfnOutput(this, "IngestFunctionArn", new CfnOutputProps
        {
            Value = ingestFunction.FunctionArn,
            Description = "ARN of the IngestClip Lambda function (configure S3 event manually)"
        });
    }
}
