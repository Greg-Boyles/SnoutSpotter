using Amazon.CDK;
using Amazon.CDK.AWS.Apigatewayv2;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class SpcConnectorStackProps : StackProps
{
    public required Repository SpcEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required string OktaIssuer { get; init; }
    public required string AllowedOrigin { get; init; }
}

public class SpcConnectorStack : Stack
{
    public SpcConnectorStack(Construct scope, string id, SpcConnectorStackProps props) : base(scope, id, props)
    {
        var ecrRepo = props.SpcEcrRepo;

        // Read from SSM — written by CoreStack, resolved at deploy time (no cross-stack dependency)
        var householdsTableName = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/households-table-name");
        var petsTableName = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/pets-table-name");
        var usersTableName = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/users-table-name");

        // Lambda function running the SPC connector API via Lambda Web Adapter
        var spcFunction = new DockerImageFunction(this, "SpcFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-spc",
            Description = "SnoutSpotter Sure Pet Care connector API",
            Code = DockerImageCode.FromEcr(ecrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 512,
            Timeout = Duration.Seconds(30),
            Environment = new Dictionary<string, string>
            {
                ["HOUSEHOLDS_TABLE"] = householdsTableName,
                ["PETS_TABLE"] = petsTableName,
                ["USERS_TABLE"] = usersTableName,
                ["OKTA_ISSUER"] = props.OktaIssuer,
                ["ALLOWED_ORIGIN"] = props.AllowedOrigin,
                ["SPC_BASE_URL"] = "https://app-api.beta.surehub.io",
                ["AWS_LWA_PORT"] = "8080"
            }
        });

        // Secrets Manager access — per-household SPC access tokens live under snoutspotter/spc/{household_id}
        spcFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "secretsmanager:CreateSecret",
                "secretsmanager:GetSecretValue",
                "secretsmanager:PutSecretValue",
                "secretsmanager:DeleteSecret",
                "secretsmanager:DescribeSecret",
                "secretsmanager:TagResource"
            },
            Resources = new[] { $"arn:aws:secretsmanager:{Region}:{Account}:secret:snoutspotter/spc/*" }
        }));

        // DynamoDB: read+update households (for spc_integration map), read+write pets (for spc_pet_id),
        // read users (for household membership check). Pets is update-only — pet deletion stays with the main API.
        spcFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "dynamodb:GetItem",
                "dynamodb:UpdateItem"
            },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-households" }
        }));

        spcFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "dynamodb:GetItem",
                "dynamodb:Query",
                "dynamodb:UpdateItem",
                "dynamodb:BatchWriteItem"
            },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-pets" }
        }));

        spcFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:GetItem" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-users" }
        }));

        // HTTP API Gateway (same shape as ApiStack so the web app can call it with the Okta bearer + X-Household-Id)
        var httpApi = new CfnApi(this, "SpcApi", new CfnApiProps
        {
            Name = "snout-spotter-spc",
            ProtocolType = "HTTP",
            CorsConfiguration = new CfnApi.CorsProperty
            {
                AllowOrigins = new[] { props.AllowedOrigin },
                AllowMethods = new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" },
                AllowHeaders = new[] { "Authorization", "Content-Type", "X-Household-Id" },
                MaxAge = 3600
            }
        });

        var integration = new CfnIntegration(this, "SpcIntegration", new CfnIntegrationProps
        {
            ApiId = httpApi.Ref,
            IntegrationType = "AWS_PROXY",
            IntegrationUri = spcFunction.FunctionArn,
            PayloadFormatVersion = "2.0"
        });

        _ = new CfnRoute(this, "DefaultRoute", new CfnRouteProps
        {
            ApiId = httpApi.Ref,
            RouteKey = "$default",
            Target = $"integrations/{integration.Ref}"
        });

        _ = new CfnStage(this, "SpcStage", new CfnStageProps
        {
            ApiId = httpApi.Ref,
            StageName = "$default",
            AutoDeploy = true
        });

        spcFunction.AddPermission("ApiGatewayInvoke", new Permission
        {
            Principal = new ServicePrincipal("apigateway.amazonaws.com"),
            SourceArn = $"arn:aws:execute-api:{Region}:{Account}:{httpApi.Ref}/*"
        });

        var apiUrl = $"https://{httpApi.AttrApiEndpoint}";

        _ = new StringParameter(this, "SpcApiUrlParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/spc/api-url",
            StringValue = apiUrl
        });

        _ = new CfnOutput(this, "SpcApiUrl", new CfnOutputProps
        {
            Value = apiUrl,
            Description = "SPC connector API Gateway endpoint URL"
        });
    }
}
