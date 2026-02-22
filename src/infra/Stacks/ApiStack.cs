using Amazon.CDK;
using Amazon.CDK.AWS.AppRunner.Alpha;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class ApiStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
    public required Table ClipsTable { get; init; }
}

public class ApiStack : Stack
{
    public ApiStack(Construct scope, string id, ApiStackProps props) : base(scope, id, props)
    {
        // ECR repository for the API Docker image
        var ecrRepo = new Repository(this, "ApiEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-api",
            RemovalPolicy = RemovalPolicy.DESTROY,
            LifecycleRules = new[]
            {
                new LifecycleRule
                {
                    MaxImageCount = 5,
                    Description = "Keep only 5 most recent images"
                }
            }
        });

        // IAM role for App Runner instance
        var instanceRole = new Role(this, "AppRunnerInstanceRole", new RoleProps
        {
            AssumedBy = new ServicePrincipal("tasks.apprunner.amazonaws.com"),
            RoleName = "snout-spotter-api-instance-role"
        });

        props.DataBucket.GrantRead(instanceRole);
        props.ClipsTable.GrantReadData(instanceRole);

        // Allow presigned URL generation (needs s3:GetObject)
        props.DataBucket.GrantRead(instanceRole, "raw-clips/*");
        props.DataBucket.GrantRead(instanceRole, "keyframes/*");

        // Allow CloudWatch read for health metrics
        instanceRole.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "cloudwatch:GetMetricData", "cloudwatch:ListMetrics" },
            Resources = new[] { "*" }
        }));

        // Outputs
        _ = new CfnOutput(this, "EcrRepoUri", new CfnOutputProps
        {
            Value = ecrRepo.RepositoryUri,
            Description = "ECR repository URI for the API"
        });

        _ = new CfnOutput(this, "InstanceRoleArn", new CfnOutputProps
        {
            Value = instanceRole.RoleArn,
            Description = "IAM role ARN for App Runner instance"
        });
    }
}
