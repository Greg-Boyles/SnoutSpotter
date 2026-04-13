using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AWS.SSM;
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
                ["MODEL_KEY"] = "models/dog-classifier/best.onnx",
                ["DETECTOR_MODEL_KEY"] = "models/yolov8m.onnx",
                ["CLASSIFIER_MODEL_KEY"] = "models/dog-classifier/best.onnx",
                ["SETTINGS_TABLE"] = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/settings-table-name")
            }
        });

        // Grant permissions
        props.DataBucket.GrantRead(inferenceFunction);
        props.ClipsTable.GrantReadWriteData(inferenceFunction);
        inferenceFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:GetItem", "dynamodb:Scan" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-settings" }
        }));

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

        // SQS queue for bulk re-run inference (triggered from API after model activation)
        var rerunDlq = new Queue(this, "RerunInferenceDlq", new QueueProps
        {
            QueueName = "snout-spotter-rerun-inference-dlq",
            RetentionPeriod = Duration.Days(7)
        });

        var rerunQueue = new Queue(this, "RerunInferenceQueue", new QueueProps
        {
            QueueName = "snout-spotter-rerun-inference",
            VisibilityTimeout = Duration.Minutes(6), // must exceed Lambda timeout (5 min)
            RetentionPeriod = Duration.Days(1),
            DeadLetterQueue = new DeadLetterQueue
            {
                Queue = rerunDlq,
                MaxReceiveCount = 2
            }
        });

        inferenceFunction.AddEventSource(new SqsEventSource(rerunQueue, new SqsEventSourceProps
        {
            BatchSize = 1,
            MaxConcurrency = 3
        }));

        _ = new StringParameter(this, "RerunQueueUrlParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/inference/rerun-queue-url",
            StringValue = rerunQueue.QueueUrl
        });

        // Output
        _ = new CfnOutput(this, "InferenceFunctionArn", new CfnOutputProps
        {
            Value = inferenceFunction.FunctionArn,
            Description = "ARN of the RunInference Lambda function"
        });
    }
}
