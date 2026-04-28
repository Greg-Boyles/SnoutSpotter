using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Events;
using Amazon.CDK.AWS.Events.Targets;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class IngestStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
    public required Table ClipsTable { get; init; }
    public required Repository IngestEcrRepo { get; init; }
    public required string ImageTag { get; init; }
}

public class IngestStack : Stack
{
    public IngestStack(Construct scope, string id, IngestStackProps props) : base(scope, id, props)
    {
        // Read from SSM (no cross-stack CDK dep): devices table lives in CoreStack,
        // burst queue lives in SpcPollerStack. Both are written there.
        var devicesTable = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/devices-table-name");
        var spcBurstQueueUrl = StringParameter.ValueForStringParameter(this, "/snoutspotter/spc-poller/burst-queue-url");
        var spcBurstQueueArn = StringParameter.ValueForStringParameter(this, "/snoutspotter/spc-poller/burst-queue-arn");

        var ingestFunction = new DockerImageFunction(this, "IngestClipFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-ingest-clip",
            Description = "Processes uploaded video clips: extracts keyframes and writes metadata to DynamoDB",
            Code = DockerImageCode.FromEcr(props.IngestEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 1024,
            Timeout = Duration.Minutes(5),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["TABLE_NAME"] = props.ClipsTable.TableName,
                ["SETTINGS_TABLE"] = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/settings-table-name"),
                ["DEVICES_TABLE"] = devicesTable,
                ["SPC_BURST_QUEUE_URL"] = spcBurstQueueUrl
            }
        });

        // Grant permissions
        props.DataBucket.GrantReadWrite(ingestFunction);
        props.ClipsTable.GrantWriteData(ingestFunction);
        ingestFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:GetItem", "dynamodb:Scan" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-settings" }
        }));

        // Cheap check "does this household have any SPC device links?" used to
        // gate motion-triggered SPC burst polling. Query + Limit=1, projection-only.
        ingestFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:Query" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-devices" }
        }));

        // SendMessage on the SPC burst queue — kicks off a 10-min poll window.
        ingestFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "sqs:SendMessage" },
            Resources = new[] { spcBurstQueueArn }
        }));

        // EventBridge rule: trigger Lambda when .mp4 uploaded to raw-clips/
        var rule = new Rule(this, "IngestClipRule", new RuleProps
        {
            RuleName = "snout-spotter-ingest-trigger",
            Description = "Trigger IngestClip Lambda when new clips are uploaded to S3",
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
                        ["key"] = new object[]
                        {
                            new Dictionary<string, string> { ["prefix"] = "raw-clips/" },
                            new Dictionary<string, string> { ["wildcard"] = "*/raw-clips/*" }
                        }
                    }
                }
            }
        });

        rule.AddTarget(new LambdaFunction(ingestFunction));

        // Output
        _ = new CfnOutput(this, "IngestFunctionArn", new CfnOutputProps
        {
            Value = ingestFunction.FunctionArn,
            Description = "ARN of the IngestClip Lambda function"
        });
    }
}
