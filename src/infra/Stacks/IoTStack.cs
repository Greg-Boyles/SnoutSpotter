using Amazon.CDK;
using Amazon.CDK.AWS.IoT;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class IoTStack : Stack
{
    public string ThingName { get; }

    public IoTStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        ThingName = "snoutspotter-pi";

        // IoT Thing representing the Pi
        var thing = new CfnThing(this, "PiThing", new CfnThingProps
        {
            ThingName = ThingName
        });

        // IoT Policy allowing shadow operations and MQTT connect
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
                        ["Resource"] = $"arn:aws:iot:{Region}:{Account}:client/{ThingName}"
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = new[] { "iot:Subscribe", "iot:Receive" },
                        ["Resource"] = new[]
                        {
                            $"arn:aws:iot:{Region}:{Account}:topicfilter/$aws/things/{ThingName}/shadow/*",
                            $"arn:aws:iot:{Region}:{Account}:topic/$aws/things/{ThingName}/shadow/*"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["Effect"] = "Allow",
                        ["Action"] = "iot:Publish",
                        ["Resource"] = $"arn:aws:iot:{Region}:{Account}:topic/$aws/things/{ThingName}/shadow/*"
                    }
                }
            }
        });

        // Outputs
        _ = new CfnOutput(this, "IoTThingName", new CfnOutputProps
        {
            Value = ThingName,
            Description = "IoT Thing name for the Pi"
        });

        _ = new CfnOutput(this, "IoTPolicyName", new CfnOutputProps
        {
            Value = iotPolicy.PolicyName!,
            Description = "IoT Policy name for the Pi"
        });
    }
}
