using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class CoreStack : Stack
{
    public Bucket DataBucket { get; }
    public Table ClipsTable { get; }
    public Table CommandsTable { get; }
    public Repository ApiEcrRepo { get; }
    public Repository IngestEcrRepo { get; }
    public Repository InferenceEcrRepo { get; }
    public Repository PiMgmtEcrRepo { get; }
    public Repository LogIngestionEcrRepo { get; }
    public Repository CommandAckEcrRepo { get; }
    public Table LabelsTable { get; }
    public Repository AutoLabelEcrRepo { get; }

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

        // GSI for listing all clips ordered by timestamp (newest first)
        ClipsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "all-by-time",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "pk", Type = AttributeType.STRING },
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

        // DynamoDB table for device command ledger
        CommandsTable = new Table(this, "CommandsTable", new TableProps
        {
            TableName = "snout-spotter-commands",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "command_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
            TimeToLiveAttribute = "ttl"
        });

        CommandsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-device",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "thing_name", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "requested_at", Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

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

        // ECR repository for Log Ingestion Lambda Docker image
        LogIngestionEcrRepo = new Repository(this, "LogIngestionEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-log-ingestion",
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

        // ECR repository for CommandAck Lambda Docker image
        CommandAckEcrRepo = new Repository(this, "CommandAckEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-command-ack",
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

        // DynamoDB table for keyframe labels (ML training data)
        LabelsTable = new Table(this, "LabelsTable", new TableProps
        {
            TableName = "snout-spotter-labels",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "keyframe_key", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        LabelsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-review",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "reviewed", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "labelled_at", Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

        LabelsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-label",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "auto_label", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "labelled_at", Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

        // ECR repository for AutoLabel Lambda Docker image
        AutoLabelEcrRepo = new Repository(this, "AutoLabelEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-auto-label",
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
