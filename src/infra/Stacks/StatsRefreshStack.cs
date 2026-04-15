using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class StatsRefreshStackProps : StackProps
{
    public required Repository StatsRefreshEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required Table ClipsTable { get; init; }
    public required Table LabelsTable { get; init; }
    public required Table StatsTable { get; init; }
    public required Table PetsTable { get; init; }
    public required string IoTThingGroupName { get; init; }
}

public class StatsRefreshStack : Stack
{
    public Function StatsRefreshFunction { get; }

    public StatsRefreshStack(Construct scope, string id, StatsRefreshStackProps props) : base(scope, id, props)
    {
        StatsRefreshFunction = new DockerImageFunction(this, "StatsRefreshFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-stats-refresh",
            Description = "Pre-computes dashboard, activity, and label stats on demand (stale-while-revalidate)",
            Code = DockerImageCode.FromEcr(props.StatsRefreshEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 512,
            Timeout = Duration.Minutes(3),
            Environment = new Dictionary<string, string>
            {
                ["TABLE_NAME"] = props.ClipsTable.TableName,
                ["LABELS_TABLE"] = props.LabelsTable.TableName,
                ["STATS_TABLE"] = props.StatsTable.TableName,
                ["IOT_THING_GROUP"] = props.IoTThingGroupName,
                ["PETS_TABLE"] = props.PetsTable.TableName
            }
        });

        props.ClipsTable.GrantReadData(StatsRefreshFunction);
        props.LabelsTable.GrantReadData(StatsRefreshFunction);
        props.StatsTable.GrantReadWriteData(StatsRefreshFunction);
        props.PetsTable.GrantReadData(StatsRefreshFunction);

        StatsRefreshFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:DescribeEndpoint" },
            Resources = new[] { "*" }
        }));
        StatsRefreshFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:GetThingShadow" },
            Resources = new[] { $"arn:aws:iot:{Region}:{Account}:thing/snoutspotter-*" }
        }));
        StatsRefreshFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:ListThingsInThingGroup" },
            Resources = new[] { $"arn:aws:iot:{Region}:{Account}:thinggroup/{props.IoTThingGroupName}" }
        }));

        // SSM parameter — allows ApiStack to read the function name without a CDK cross-stack dependency
        _ = new StringParameter(this, "StatsRefreshFunctionNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/stats-refresh/function-name",
            StringValue = StatsRefreshFunction.FunctionName
        });

        _ = new CfnOutput(this, "StatsRefreshFunctionArn", new CfnOutputProps
        {
            Value = StatsRefreshFunction.FunctionArn,
            Description = "ARN of the StatsRefresh Lambda function"
        });
    }
}
