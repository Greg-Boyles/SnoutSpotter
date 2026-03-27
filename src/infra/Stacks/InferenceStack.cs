using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class InferenceStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
    public required Table ClipsTable { get; init; }
    public required Repository InferenceEcrRepo { get; init; }
    public required string ImageTag { get; init; }
}

public class InferenceStack : Stack
{
    public InferenceStack(Construct scope, string id, InferenceStackProps props) : base(scope, id, props)
    {
        var inferenceFunction = new DockerImageFunction(this, "RunInferenceFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-run-inference",
            Description = "Runs ML inference on video keyframes to detect and classify dogs",
            Code = DockerImageCode.FromEcr(props.InferenceEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 2048,
            Timeout = Duration.Minutes(5),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["TABLE_NAME"] = props.ClipsTable.TableName,
                ["DETECTOR_MODEL_KEY"] = "models/dog-detector/best.onnx",
                ["CLASSIFIER_MODEL_KEY"] = "models/dog-classifier/best.onnx"
            }
        });

        // Grant permissions
        props.DataBucket.GrantRead(inferenceFunction);
        props.ClipsTable.GrantReadWriteData(inferenceFunction);
    }
}
