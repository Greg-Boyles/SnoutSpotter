using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Logs;
using Amazon.CDK.AWS.S3;
using Constructs;
using IoT = Amazon.CDK.AWS.IoT;

namespace SnoutSpotter.Infra.Stacks;

public class IoTStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
}

public class IoTStack : Stack
{
    public string ThingGroupName { get; }
    public string PolicyName { get; }
    public string PiLogGroupName { get; }
    public string RoleAliasName { get; }

    public IoTStack(Construct scope, string id, IoTStackProps props) : base(scope, id, props)
    {
        ThingGroupName = "snoutspotter-pis";
        PolicyName = "snoutspotter-pi-policy";
        PiLogGroupName = "/snoutspotter/pi-logs";
        RoleAliasName = "snoutspotter-pi-role-alias";

        // Thing Group for all SnoutSpotter Pi devices
        // Individual things will be registered dynamically via Pi Management API
        var thingGroup = new IoT.CfnThingGroup(this, "PiThingGroup", new IoT.CfnThingGroupProps
        {
            ThingGroupName = ThingGroupName,
            ThingGroupProperties = new IoT.CfnThingGroup.ThingGroupPropertiesProperty
            {
                ThingGroupDescription = "SnoutSpotter Raspberry Pi devices"
            }
        });

        // IAM Role assumable via IoT Credentials Provider
        var piCredentialsRole = new Role(this, "PiCredentialsRole", new RoleProps
        {
            RoleName = "snoutspotter-pi-credentials",
            AssumedBy = new ServicePrincipal("credentials.iot.amazonaws.com"),
        });

        props.DataBucket.GrantPut(piCredentialsRole, "raw-clips/*");
        props.DataBucket.GrantRead(piCredentialsRole, "releases/*");
        piCredentialsRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[] { "cloudwatch:PutMetricData" },
            Resources = new[] { "*" },
            Conditions = new Dictionary<string, object>
            {
                ["StringEquals"] = new Dictionary<string, string>
                {
                    ["cloudwatch:namespace"] = "SnoutSpotter"
                }
            }
        }));

        // KVS permissions for live streaming (kvssink pushes media to a KVS stream)
        piCredentialsRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Actions = new[]
            {
                "kinesisvideo:CreateStream",
                "kinesisvideo:DescribeStream",
                "kinesisvideo:PutMedia",
                "kinesisvideo:GetDataEndpoint",
                "kinesisvideo:TagStream",
            },
            Resources = new[] { $"arn:aws:kinesisvideo:{Region}:{Account}:stream/snoutspotter-*" }
        }));

        // IoT Role Alias — bridge between IoT X.509 certs and IAM temporary credentials
        var roleAlias = new IoT.CfnRoleAlias(this, "PiRoleAlias", new IoT.CfnRoleAliasProps
        {
            RoleAlias = RoleAliasName,
            RoleArn = piCredentialsRole.RoleArn,
            CredentialDurationSeconds = 3600
        });

        // IoT Policy: uses ${iot:Connection.Thing.ThingName} variable so any
        // registered thing can connect and manage its own shadow and publish logs
        var iotPolicy = new IoT.CfnPolicy(this, "PiPolicy", new IoT.CfnPolicyProps
        {
            PolicyName = PolicyName,
            PolicyDocument = new Dictionary<string, object>
            {
                ["Version"] = "2012-10-17",
                ["Statement"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:Connect",
                        ["Resource"] = $"arn:aws:iot:{Region}:{Account}:client/${{iot:Connection.Thing.ThingName}}*"
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = new[] { "iot:Subscribe", "iot:Receive" },
                        ["Resource"] = new[]
                        {
                            $"arn:aws:iot:{Region}:{Account}:topicfilter/$aws/things/${{iot:Connection.Thing.ThingName}}/shadow/*",
                            $"arn:aws:iot:{Region}:{Account}:topic/$aws/things/${{iot:Connection.Thing.ThingName}}/shadow/*",
                            $"arn:aws:iot:{Region}:{Account}:topicfilter/snoutspotter/${{iot:Connection.Thing.ThingName}}/commands",
                            $"arn:aws:iot:{Region}:{Account}:topic/snoutspotter/${{iot:Connection.Thing.ThingName}}/commands"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:Publish",
                        ["Resource"] = new[]
                        {
                            $"arn:aws:iot:{Region}:{Account}:topic/$aws/things/${{iot:Connection.Thing.ThingName}}/shadow/*",
                            $"arn:aws:iot:{Region}:{Account}:topic/snoutspotter/${{iot:Connection.Thing.ThingName}}/logs"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:AssumeRoleWithCertificate",
                        ["Resource"] = $"arn:aws:iot:{Region}:{Account}:rolealias/{RoleAliasName}"
                    }
                }
            }
        });

        // CloudWatch Log Group for Pi device logs (per-device streams created by LogIngestion Lambda)
        var piLogGroup = new LogGroup(this, "PiLogsGroup", new LogGroupProps
        {
            LogGroupName = PiLogGroupName,
            Retention = RetentionDays.ONE_WEEK,
            RemovalPolicy = RemovalPolicy.DESTROY
        });

        // Outputs
        _ = new CfnOutput(this, "IoTThingGroupName", new CfnOutputProps
        {
            Value = ThingGroupName,
            Description = "IoT Thing Group for SnoutSpotter Pi devices"
        });

        _ = new CfnOutput(this, "IoTPolicyName", new CfnOutputProps
        {
            Value = iotPolicy.PolicyName!,
            Description = "IoT Policy name for Pi devices"
        });

        _ = new CfnOutput(this, "PiLogGroupNameOutput", new CfnOutputProps
        {
            Value = PiLogGroupName,
            Description = "CloudWatch Log Group for Pi device logs"
        });
    }
}
