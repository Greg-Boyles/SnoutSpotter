using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class AutoLabelStackProps : StackProps
{
    public required Repository AutoLabelEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required Bucket DataBucket { get; init; }
    public required Table LabelsTable { get; init; }
}

public class AutoLabelStack : Stack
{
    public Function AutoLabelFunction { get; }

    public AutoLabelStack(Construct scope, string id, AutoLabelStackProps props) : base(scope, id, props)
    {
        AutoLabelFunction = new DockerImageFunction(this, "AutoLabelFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-auto-label",
            Description = "Auto-labels keyframes using pre-trained YOLOv8 dog detection",
            Code = DockerImageCode.FromEcr(props.AutoLabelEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 2048,
            Timeout = Duration.Minutes(5),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["LABELS_TABLE"] = props.LabelsTable.TableName,
                ["MODEL_KEY"] = "models/yolov8n.onnx"
            }
        });

        props.DataBucket.GrantRead(AutoLabelFunction);
        props.LabelsTable.GrantReadWriteData(AutoLabelFunction);

        _ = new CfnOutput(this, "AutoLabelFunctionArn", new CfnOutputProps
        {
            Value = AutoLabelFunction.FunctionArn,
            Description = "ARN of the AutoLabel Lambda function"
        });
    }
}
