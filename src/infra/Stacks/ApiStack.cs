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
    public required Table CommandsTable { get; init; }
    public required Table LabelsTable { get; init; }
    public required Repository ApiEcrRepo { get; init; }
    public string AutoLabelFunctionName { get; init; } = "snout-spotter-auto-label";
    public required string ImageTag { get; init; }
    public string IoTThingGroupName { get; init; } = "snoutspotter-pis";
    public required string OktaIssuer { get; init; }
    public required string AllowedOrigin { get; init; }
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
                ["IOT_THING_GROUP"] = props.IoTThingGroupName,
                ["OKTA_ISSUER"] = props.OktaIssuer,
                ["ALLOWED_ORIGIN"] = props.AllowedOrigin,
                ["PI_LOG_GROUP"] = "/snoutspotter/pi-logs",
                ["COMMANDS_TABLE"] = props.CommandsTable.TableName,
                ["LABELS_TABLE"] = props.LabelsTable.TableName,
                ["AUTO_LABEL_FUNCTION"] = props.AutoLabelFunctionName
            }
        });

        // Grant permissions
        props.CommandsTable.GrantReadWriteData(apiFunction);
        props.LabelsTable.GrantReadWriteData(apiFunction);
        props.DataBucket.GrantRead(apiFunction);
        props.DataBucket.GrantPut(apiFunction, "training-uploads/*");
        props.ClipsTable.GrantReadData(apiFunction);
        props.DataBucket.GrantRead(apiFunction, "raw-clips/*");
        props.DataBucket.GrantRead(apiFunction, "keyframes/*");

        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "cloudwatch:GetMetricData", "cloudwatch:ListMetrics" },
            Resources = new[] { "*" }
        }));

        // CloudWatch Logs permissions for querying Pi device logs
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "logs:StartQuery", "logs:GetQueryResults", "logs:StopQuery" },
            Resources = new[] { $"arn:aws:logs:{Region}:{Account}:log-group:/snoutspotter/pi-logs:*" }
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

        // Lambda invoke for auto-label trigger
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "lambda:InvokeFunction" },
            Resources = new[] { $"arn:aws:lambda:{Region}:{Account}:function:{props.AutoLabelFunctionName}" }
        }));

        // IoT Publish for device commands
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:Publish" },
            Resources = new[] { $"arn:aws:iot:{Region}:{Account}:topic/snoutspotter/*/commands" }
        }));

        // IoT DescribeEndpoint (global action, no resource-level scoping)
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "iot:DescribeEndpoint" },
            Resources = new[] { "*" }
        }));

        // KVS permissions for live streaming (HLS playback from kvssink streams)
        apiFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "kinesisvideo:DescribeStream",
                "kinesisvideo:GetDataEndpoint",
                "kinesisvideo:GetHLSStreamingSessionURL",
            },
            Resources = new[] { $"arn:aws:kinesisvideo:{Region}:{Account}:stream/snoutspotter-*" }
        }));

        // HTTP API Gateway
        var httpApi = new CfnApi(this, "ApiGateway", new CfnApiProps
        {
            Name = "snout-spotter-api",
            ProtocolType = "HTTP",
            CorsConfiguration = new CfnApi.CorsProperty
            {
                AllowOrigins = new[] { props.AllowedOrigin },
                AllowMethods = new[] { "GET", "POST", "PUT", "DELETE", "OPTIONS" },
                AllowHeaders = new[] { "Authorization", "Content-Type" },
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
