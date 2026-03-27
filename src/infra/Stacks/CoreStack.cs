using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class CoreStack : Stack
{
    public Bucket DataBucket { get; }
    public Table ClipsTable { get; }
    public Repository ApiEcrRepo { get; }
    public Repository IngestEcrRepo { get; }
    public Repository InferenceEcrRepo { get; }
    public Repository PiMgmtEcrRepo { get; }

    public CoreStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        // S3 bucket for all data: clips, keyframes, labeled data, models
        DataBucket = new Bucket(this, "DataBucket", new BucketProps
        {
            BucketName = $"snout-spotter-{Account}",
            RemovalPolicy = RemovalPolicy.RETAIN,
            Versioned = false,
            Encryption = BucketEncryption.S3_MANAGED,
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            EventBridgeEnabled = true,
            LifecycleRules = new[]
            {
                new Amazon.CDK.AWS.S3.LifecycleRule
                {
                    Id = "TransitionToIA",
                    Prefix = "raw-clips/",
                    Transitions = new[]
                    {
                        new Transition
                        {
                            StorageClass = StorageClass.INFREQUENT_ACCESS,
                            TransitionAfter = Duration.Days(30)
                        },
                        new Transition
                        {
                            StorageClass = StorageClass.GLACIER,
                            TransitionAfter = Duration.Days(90)
                        }
                    }
                }
            },
            Cors = new[]
            {
                new CorsRule
                {
                    AllowedMethods = new[] { HttpMethods.GET },
                    AllowedOrigins = new[] { "*" },
                    AllowedHeaders = new[] { "*" },
                    MaxAge = 3600
                }
            }
        });

        // DynamoDB table for clip metadata
        ClipsTable = new Table(this, "ClipsTable", new TableProps
        {
            TableName = "snout-spotter-clips",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "clip_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.RETAIN,
            PointInTimeRecovery = true
        });

        // GSI for querying by date
        ClipsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-date",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "date", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "timestamp", Type = AttributeType.NUMBER },
            ProjectionType = ProjectionType.ALL
        });

        // GSI for querying detections
        ClipsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-detection",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "detection_type", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "timestamp", Type = AttributeType.NUMBER },
            ProjectionType = ProjectionType.ALL
        });

        // IAM user for Pi Zero to upload clips
        var piUser = new User(this, "PiUser", new UserProps
        {
            UserName = "snout-spotter-pi"
        });

        DataBucket.GrantPut(piUser, "raw-clips/*");

        // Also allow the Pi to put CloudWatch metrics
        piUser.AddToPolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
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

        // ECR repository for the API Docker image (created here so it exists before ApiStack needs it)
        ApiEcrRepo = new Repository(this, "ApiEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-api",
            RemovalPolicy = RemovalPolicy.DESTROY,
            LifecycleRules = new[]
            {
                new Amazon.CDK.AWS.ECR.LifecycleRule
                {
                    MaxImageCount = 5,
                    Description = "Keep only 5 most recent images"
                }
            }
        });

        // ECR repository for IngestClip Lambda Docker image
        IngestEcrRepo = new Repository(this, "IngestEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-ingest",
            RemovalPolicy = RemovalPolicy.DESTROY,
            LifecycleRules = new[]
            {
                new Amazon.CDK.AWS.ECR.LifecycleRule
                {
                    MaxImageCount = 3,
                    Description = "Keep only 3 most recent images"
                }
            }
        });

        // ECR repository for RunInference Lambda Docker image
        InferenceEcrRepo = new Repository(this, "InferenceEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-inference",
            RemovalPolicy = RemovalPolicy.DESTROY,
            LifecycleRules = new[]
            {
                new Amazon.CDK.AWS.ECR.LifecycleRule
                {
                    MaxImageCount = 3,
                    Description = "Keep only 3 most recent images"
                }
            }
        });

        // ECR repository for Pi Management Lambda Docker image
        PiMgmtEcrRepo = new Repository(this, "PiMgmtEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-pi-mgmt",
            RemovalPolicy = RemovalPolicy.DESTROY,
            LifecycleRules = new[]
            {
                new Amazon.CDK.AWS.ECR.LifecycleRule
                {
                    MaxImageCount = 3,
                    Description = "Keep only 3 most recent images"
                }
            }
        });

        // Outputs
        _ = new CfnOutput(this, "ApiEcrRepoUri", new CfnOutputProps
        {
            Value = ApiEcrRepo.RepositoryUri,
            Description = "ECR repository URI for the API"
        });

        _ = new CfnOutput(this, "DataBucketName", new CfnOutputProps
        {
            Value = DataBucket.BucketName,
            Description = "S3 bucket for SnoutSpotter data"
        });

        _ = new CfnOutput(this, "ClipsTableName", new CfnOutputProps
        {
            Value = ClipsTable.TableName,
            Description = "DynamoDB table for clip metadata"
        });
    }
}
