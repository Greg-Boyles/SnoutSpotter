using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class ApiStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
    public required Table ClipsTable { get; init; }
    public required Repository ApiEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public string IoTThingGroupName { get; init; } = "snoutspotter-pis";
}

public class ApiStack : Stack
{
    public ApiStack(Construct scope, string id, ApiStackProps props) : base(scope, id, props)
    {
        var ecrRepo = props.ApiEcrRepo;

        // Lambda function running ASP.NET Core API via Lambda Web Adapter
        var apiFunction = new DockerImageFunction(this, "ApiFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-api",
            Description = "SnoutSpotter ASP.NET Core API via Lambda Web Adapter",
            Code = DockerImageCode.FromEcr(ecrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 512,
            Timeout = Duration.Seconds(30),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["TABLE_NAME"] = props.ClipsTable.TableName,
                ["AWS_LWA_PORT"] = "8080",
                ["IOT_THING_GROUP"] = props.IoTThingGroupName
            }
        });

        // Grant permissions
        props.DataBucket.GrantRead(apiFunction);
        props.ClipsTable.GrantReadData(apiFunction);
        props.DataBucket.GrantRead(apiFunction, "raw-clips/*");
        props.DataBucket.GrantRead(apiFunction, "keyframes/*");

        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "cloudwatch:GetMetricData", "cloudwatch:ListMetrics" },
            Resources = new[] { "*" }
        }));

        // IoT Device Shadow permissions (all snoutspotter things)
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:GetThingShadow", "iot:UpdateThingShadow" },
            Resources = new[] { $"arn:aws:iot:{Region}:{Account}:thing/snoutspotter-*" }
        }));

        // IoT list things in group
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:ListThingsInThingGroup" },
            Resources = new[] { $"arn:aws:iot:{Region}:{Account}:thinggroup/{props.IoTThingGroupName}" }
        }));

        // IoT DescribeEndpoint (global action, no resource-level scoping)
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:DescribeEndpoint" },
            Resources = new[] { "*" }
        }));

        // HTTP API Gateway
        var httpApi = new CfnApi(this, "ApiGateway", new CfnApiProps
        {
            Name = "snout-spotter-api",
            ProtocolType = "HTTP",
            CorsConfiguration = new CfnApi.CorsProperty
            {
                AllowOrigins = new[] { "*" },
                AllowMethods = new[] { "GET", "POST", "PUT", "DELETE", "OPTIONS" },
                AllowHeaders = new[] { "*" },
                MaxAge = 3600
            }
        });

        // Lambda integration
        var integration = new CfnIntegration(this, "ApiIntegration", new CfnIntegrationProps
        {
            ApiId = httpApi.Ref,
            IntegrationType = "AWS_PROXY",
            IntegrationUri = apiFunction.FunctionArn,
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
        _ = new CfnStage(this, "ApiStage", new CfnStageProps
        {
            ApiId = httpApi.Ref,
            StageName = "$default",
            AutoDeploy = true
        });

        // Grant API Gateway permission to invoke Lambda
        apiFunction.AddPermission("ApiGatewayInvoke", new Permission
        {
            Principal = new ServicePrincipal("apigateway.amazonaws.com"),
            SourceArn = $"arn:aws:execute-api:{Region}:{Account}:{httpApi.Ref}/*"
        });

        // Outputs
        _ = new CfnOutput(this, "ApiUrl", new CfnOutputProps
        {
            Value = $"https://{httpApi.AttrApiEndpoint}",
            Description = "API Gateway endpoint URL"
        });
    }
}
