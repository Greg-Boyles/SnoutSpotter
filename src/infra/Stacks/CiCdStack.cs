using Amazon.CDK;
using Amazon.CDK.AWS.IAM;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class CiCdStack : Stack
{
    public CiCdStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        // Import existing GitHub OIDC Identity Provider
        var oidcProviderArn = $"arn:aws:iam::{Account}:oidc-provider/token.actions.githubusercontent.com";
        var oidcProvider = OpenIdConnectProvider.FromOpenIdConnectProviderArn(this, "GitHubOidc", oidcProviderArn);

        // IAM Role for GitHub Actions
        var deployRole = new Role(this, "GitHubActionsRole", new RoleProps
        {
            RoleName = "snout-spotter-github-actions",
            Description = "Role assumed by GitHub Actions to deploy SnoutSpotter",
            MaxSessionDuration = Duration.Hours(1),
            AssumedBy = new FederatedPrincipal(
                oidcProvider.OpenIdConnectProviderArn,
                new Dictionary<string, object>
                {
                    ["StringEquals"] = new Dictionary<string, string>
                    {
                        ["token.actions.githubusercontent.com:aud"] = "sts.amazonaws.com"
                    },
                    ["StringLike"] = new Dictionary<string, string>
                    {
                        ["token.actions.githubusercontent.com:sub"] = "repo:Greg-Boyles/SnoutSpotter:*"
                    }
                },
                "sts:AssumeRoleWithWebIdentity"
            )
        });

        // ECR permissions (build & push Docker images)
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "ecr:GetAuthorizationToken",
                "ecr-public:GetAuthorizationToken",
                "sts:GetServiceBearerToken"
            },
            Resources = new[] { "*" }
        }));

        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "ecr:BatchCheckLayerAvailability",
                "ecr:GetDownloadUrlForLayer",
                "ecr:BatchGetImage",
                "ecr:PutImage",
                "ecr:InitiateLayerUpload",
                "ecr:UploadLayerPart",
                "ecr:CompleteLayerUpload"
            },
            Resources = new[] { $"arn:aws:ecr:{Region}:{Account}:repository/snout-spotter-*" }
        }));

        // S3 permissions (deploy web frontend, upload ML artifacts)
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "s3:PutObject",
                "s3:GetObject",
                "s3:DeleteObject",
                "s3:ListBucket"
            },
            Resources = new[]
            {
                $"arn:aws:s3:::snout-spotter-web-{Account}",
                $"arn:aws:s3:::snout-spotter-web-{Account}/*",
                $"arn:aws:s3:::snout-spotter-{Account}",
                $"arn:aws:s3:::snout-spotter-{Account}/models/*",
                $"arn:aws:s3:::snout-spotter-{Account}/training-packages/*"
            }
        }));

        // CloudFront permissions (invalidate cache after web deploy)
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "cloudfront:CreateInvalidation" },
            Resources = new[] { $"arn:aws:cloudfront::{Account}:distribution/*" }
        }));

        // Lambda permissions (deploy functions)
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "lambda:UpdateFunctionCode",
                "lambda:UpdateFunctionConfiguration",
                "lambda:GetFunction",
                "lambda:PublishVersion"
            },
            Resources = new[] { $"arn:aws:lambda:{Region}:{Account}:function:snout-spotter-*" }
        }));

        // CDK / CloudFormation permissions (deploy infra)
        deployRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[]
            {
                "cloudformation:*",
                "ssm:GetParameter",
                "sts:AssumeRole"
            },
            Resources = new[] { "*" }
        }));

        // Outputs
        _ = new CfnOutput(this, "GitHubActionsRoleArn", new CfnOutputProps
        {
            Value = deployRole.RoleArn,
            Description = "ARN of the IAM role for GitHub Actions (set as AWS_ROLE_ARN secret)"
        });
    }
}
