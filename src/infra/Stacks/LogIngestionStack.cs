using Amazon.CDK;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Logs;
using Constructs;
using IoT = Amazon.CDK.AWS.IoT;

namespace SnoutSpotter.Infra.Stacks;

public class LogIngestionStackProps : StackProps
{
    public required Repository LogIngestionEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required string PiLogGroupName { get; init; }
}

public class LogIngestionStack : Stack
{
    public LogIngestionStack(Construct scope, string id, LogIngestionStackProps props) : base(scope, id, props)
    {
        // Import the log group created by IoTStack
        var piLogGroup = LogGroup.FromLogGroupName(this, "PiLogsGroup", props.PiLogGroupName);

        // Log ingestion Lambda: routes MQTT log messages to per-device CloudWatch streams
        var logIngestionFunction = new DockerImageFunction(this, "LogIngestionFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-log-ingestion",
            Description = "Routes Pi device logs from MQTT to per-device CloudWatch log streams",
            Code = DockerImageCode.FromEcr(props.LogIngestionEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 256,
            Timeout = Duration.Seconds(15),
            Environment = new Dictionary<string, string>
            {
                ["LOG_GROUP_NAME"] = props.PiLogGroupName
            }
        });

        // Grant Lambda permissions to write logs and create streams
        piLogGroup.GrantWrite(logIngestionFunction);
        logIngestionFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "logs:CreateLogStream", "logs:DescribeLogStreams" },
            Resources = new[] { $"arn:aws:logs:{Region}:{Account}:log-group:{props.PiLogGroupName}:*" }
        }));

        // Allow IoT Rule to invoke the Lambda
        logIngestionFunction.AddPermission("IoTRuleInvoke", new Permission
        {
            Principal = new ServicePrincipal("iot.amazonaws.com"),
            SourceArn = $"arn:aws:iot:{Region}:{Account}:rule/snoutspotter_pi_logs"
        });

        // IoT Topic Rule: routes MQTT log messages to Lambda for per-device stream routing
        var logRule = new IoT.CfnTopicRule(this, "PiLogRule", new IoT.CfnTopicRuleProps
        {
            RuleName = "snoutspotter_pi_logs",
            TopicRulePayload = new IoT.CfnTopicRule.TopicRulePayloadProperty
            {
                Sql = "SELECT * FROM 'snoutspotter/+/logs'",
                Actions = new[]
                {
                    new IoT.CfnTopicRule.ActionProperty
                    {
                        Lambda = new IoT.CfnTopicRule.LambdaActionProperty
                        {
                            FunctionArn = logIngestionFunction.FunctionArn
                        }
                    }
                },
                RuleDisabled = false
            }
        });

        // Output
        _ = new CfnOutput(this, "LogIngestionFunctionArn", new CfnOutputProps
        {
            Value = logIngestionFunction.FunctionArn,
            Description = "ARN of the Log Ingestion Lambda function"
        });
    }
}
