using Amazon.CDK;
using Amazon.CDK.AWS.IoT;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class IoTStack : Stack
{
    public string ThingGroupName { get; }

    public IoTStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        ThingGroupName = "snoutspotter-pis";

        // Thing Group for all SnoutSpotter Pi devices
        // Individual things will be registered dynamically via Pi Management API
        var thingGroup = new CfnThingGroup(this, "PiThingGroup", new CfnThingGroupProps
        {
            ThingGroupName = ThingGroupName,
            ThingGroupProperties = new CfnThingGroup.ThingGroupPropertiesProperty
            {
                ThingGroupDescription = "SnoutSpotter Raspberry Pi devices"
            }
        });

        // IoT Policy: uses ${iot:Connection.Thing.ThingName} variable so any
        // registered thing can connect and manage its own shadow
        var iotPolicy = new CfnPolicy(this, "PiPolicy", new CfnPolicyProps
        {
            PolicyName = "snoutspotter-pi-policy",
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
                            $"arn:aws:iot:{Region}:{Account}:topic/$aws/things/${{iot:Connection.Thing.ThingName}}/shadow/*"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:Publish",
                        ["Resource"] = $"arn:aws:iot:{Region}:{Account}:topic/$aws/things/${{iot:Connection.Thing.ThingName}}/shadow/*"
                    }
                }
            }
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
    }
}
