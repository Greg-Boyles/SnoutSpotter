using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Constructs;
using IoT = Amazon.CDK.AWS.IoT;

namespace SnoutSpotter.Infra.Stacks;

public class CommandAckStackProps : StackProps
{
    public required Repository CommandAckEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required Table CommandsTable { get; init; }
}

public class CommandAckStack : Stack
{
    public CommandAckStack(Construct scope, string id, CommandAckStackProps props) : base(scope, id, props)
    {
        var commandAckFunction = new DockerImageFunction(this, "CommandAckFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-command-ack",
            Description = "Updates command ledger in DynamoDB when Pi devices ack commands",
            Code = DockerImageCode.FromEcr(props.CommandAckEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 256,
            Timeout = Duration.Seconds(10),
            Environment = new Dictionary<string, string>
            {
                ["COMMANDS_TABLE"] = props.CommandsTable.TableName
            }
        });

        props.CommandsTable.GrantWriteData(commandAckFunction);

        // Allow IoT Rule to invoke the Lambda
        commandAckFunction.AddPermission("IoTRuleInvoke", new Permission
        {
            Principal = new ServicePrincipal("iot.amazonaws.com"),
            SourceArn = $"arn:aws:iot:{Region}:{Account}:rule/snoutspotter_command_ack"
        });

        // IoT Topic Rule: routes command ack messages to Lambda
        _ = new IoT.CfnTopicRule(this, "CommandAckRule", new IoT.CfnTopicRuleProps
        {
            RuleName = "snoutspotter_command_ack",
            TopicRulePayload = new IoT.CfnTopicRule.TopicRulePayloadProperty
            {
                Sql = "SELECT * FROM 'snoutspotter/+/commands/ack'",
                AwsIotSqlVersion = "2016-03-23",
                Actions = new[]
                {
                    new IoT.CfnTopicRule.ActionProperty
                    {
                        Lambda = new IoT.CfnTopicRule.LambdaActionProperty
                        {
                            FunctionArn = commandAckFunction.FunctionArn
                        }
                    }
                },
                RuleDisabled = false
            }
        });

        _ = new CfnOutput(this, "CommandAckFunctionArn", new CfnOutputProps
        {
            Value = commandAckFunction.FunctionArn,
            Description = "ARN of the CommandAck Lambda function"
        });
    }
}
