using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class PiMgmtStackProps : StackProps
{
    public required Repository PiMgmtEcrRepo { get; init; }
    public required string ImageTag { get; init; }

}

public class PiMgmtStack : Stack
{
    public PiMgmtStack(Construct scope, string id, PiMgmtStackProps props) : base(scope, id, props)
    {
        var ecrRepo = props.PiMgmtEcrRepo;

        // Read from SSM — written by IoTStack, resolved at deploy time (no cross-stack dependency)
        var iotThingGroupName = StringParameter.ValueForStringParameter(this, "/snoutspotter/iot/thing-group-name");
        var iotPolicyName = StringParameter.ValueForStringParameter(this, "/snoutspotter/iot/policy-name");
        var trainerThingGroupName = StringParameter.ValueForStringParameter(this, "/snoutspotter/iot/trainer-thing-group-name");
        var trainerPolicyName = StringParameter.ValueForStringParameter(this, "/snoutspotter/iot/trainer-policy-name");
        var dataBucketName = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/data-bucket-name");
        var trainingJobQueueUrl = StringParameter.ValueForStringParameter(this, "/snoutspotter/training/job-queue-url");

        // Lambda function for Pi Management API
        var piMgmtFunction = new DockerImageFunction(this, "PiMgmtFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-pi-mgmt",
            Description = "SnoutSpotter Pi Management API for device registration",
            Code = DockerImageCode.FromEcr(ecrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 512,
            Timeout = Duration.Seconds(30),
            Environment = new Dictionary<string, string>
            {
                ["IOT_THING_GROUP"] = iotThingGroupName,
                ["IOT_POLICY_NAME"] = iotPolicyName,
                ["IOT_TRAINER_THING_GROUP"] = trainerThingGroupName,
                ["IOT_TRAINER_POLICY_NAME"] = trainerPolicyName,
                ["DATA_BUCKET"] = dataBucketName,
                ["TRAINING_JOB_QUEUE_URL"] = trainingJobQueueUrl,
                ["AWS_LWA_PORT"] = "8080"
            }
        });

        // IAM permissions for IoT control plane operations
        piMgmtFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "iot:CreateThing",
                "iot:DeleteThing",
                "iot:CreateKeysAndCertificate",
                "iot:DeleteCertificate",
                "iot:UpdateCertificate",
                "iot:AttachPolicy",
                "iot:DetachPolicy",
                "iot:AttachThingPrincipal",
                "iot:DetachThingPrincipal",
                "iot:AddThingToThingGroup",
                "iot:RemoveThingFromThingGroup",
                "iot:ListThingPrincipals",
                "iot:ListThingsInThingGroup",
                "iot:DescribeEndpoint"
            },
            Resources = new[] { "*" } // IoT control plane doesn't support resource-level permissions
        }));

        // HTTP API Gateway for Pi Management
        var httpApi = new CfnApi(this, "PiMgmtApi", new CfnApiProps
        {
            Name = "snout-spotter-pi-mgmt",
            ProtocolType = "HTTP",
            CorsConfiguration = new CfnApi.CorsProperty
            {
                AllowOrigins = new[] { "*" },
                AllowMethods = new[] { "GET", "POST", "DELETE", "OPTIONS" },
                AllowHeaders = new[] { "*" },
                MaxAge = 3600
            }
        });

        // Lambda integration
        var integration = new CfnIntegration(this, "PiMgmtIntegration", new CfnIntegrationProps
        {
            ApiId = httpApi.Ref,
            IntegrationType = "AWS_PROXY",
            IntegrationUri = piMgmtFunction.FunctionArn,
            PayloadFormatVersion = "2.0"
        });

        // Default route: proxy all requests to Lambda
        _ = new CfnRoute(this, "DefaultRoute", new CfnRouteProps
        {
            ApiId = httpApi.Ref,
            RouteKey = "$default",
            Target = $"integrations/{integration.Ref}"
        });

        // Auto-deploy stage
        _ = new CfnStage(this, "PiMgmtStage", new CfnStageProps
        {
            ApiId = httpApi.Ref,
            StageName = "$default",
            AutoDeploy = true
        });

        // Grant API Gateway permission to invoke Lambda
        piMgmtFunction.AddPermission("ApiGatewayInvoke", new Permission
        {
            Principal = new ServicePrincipal("apigateway.amazonaws.com"),
            SourceArn = $"arn:aws:execute-api:{Region}:{Account}:{httpApi.Ref}/*"
        });

        // Outputs
        _ = new CfnOutput(this, "PiMgmtApiUrl", new CfnOutputProps
        {
            Value = $"https://{httpApi.AttrApiEndpoint}",
            Description = "Pi Management API Gateway endpoint URL"
        });
    }
}
