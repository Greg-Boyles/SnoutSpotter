using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
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
                ["CLASSIFIER_MODEL_KEY"] = "models/dog-classifier/best.onnx"
            }
        });

        // Grant permissions
        props.DataBucket.GrantRead(inferenceFunction);
        props.ClipsTable.GrantReadWriteData(inferenceFunction);

        // EventBridge rule: trigger Lambda when keyframe JPGs are uploaded
        var rule = new Rule(this, "InferenceKeyframeRule", new RuleProps
        {
            RuleName = "snout-spotter-inference-trigger",
            Description = "Trigger RunInference Lambda when keyframes are uploaded to S3",
            EventPattern = new EventPattern
            {
                Source = new[] { "aws.s3" },
                DetailType = new[] { "Object Created" },
                Detail = new Dictionary<string, object>
                {
                    ["bucket"] = new Dictionary<string, object>
                    {
                        ["name"] = new[] { props.DataBucket.BucketName }
                    },
                    ["object"] = new Dictionary<string, object>
                    {
                        ["key"] = new[] { new Dictionary<string, string> { ["prefix"] = "keyframes/" } }
                    }
                }
            }
        });

        rule.AddTarget(new LambdaFunction(inferenceFunction));

        // Output
        _ = new CfnOutput(this, "InferenceFunctionArn", new CfnOutputProps
        {
            Value = inferenceFunction.FunctionArn,
            Description = "ARN of the RunInference Lambda function"
        });
    }
}
