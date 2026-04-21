using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SSM;
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
    public Table ExportsTable { get; }
    public Table TrainingJobsTable { get; }
    public Table SettingsTable { get; }
    public Table ModelsTable { get; }
    public Repository AutoLabelEcrRepo { get; }
    public Repository ExportDatasetEcrRepo { get; }
    public Repository TrainingAgentEcrRepo { get; }
    public Repository UpdateTrainingProgressEcrRepo { get; }
    public Repository StatsRefreshEcrRepo { get; }
    public Repository SpcEcrRepo { get; }
    public Table StatsTable { get; }
    public Table PetsTable { get; }
    public Table UsersTable { get; }
    public Table HouseholdsTable { get; }
    public Table DevicesTable { get; }

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
                    AllowedMethods = new[] { HttpMethods.GET, HttpMethods.PUT },
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

        // GSI for querying clips by device
        ClipsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-device",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "device", Type = AttributeType.STRING },
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

        LabelsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-confirmed-label",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "confirmed_label", Type = AttributeType.STRING },
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

        // DynamoDB table for training dataset export manifests
        ExportsTable = new Table(this, "ExportsTable", new TableProps
        {
            TableName = "snout-spotter-exports",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "export_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        // ECR repository for ExportDataset Lambda
        ExportDatasetEcrRepo = new Repository(this, "ExportDatasetEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-export-dataset",
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

        // DynamoDB table for training job metadata
        TrainingJobsTable = new Table(this, "TrainingJobsTable", new TableProps
        {
            TableName = "snout-spotter-training-jobs",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "job_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        TrainingJobsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-status",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "status", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "created_at", Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

        // DynamoDB table for server-side settings (Lambda processing config)
        SettingsTable = new Table(this, "SettingsTable", new TableProps
        {
            TableName = "snout-spotter-settings",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "setting_key", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        // DynamoDB table for ML model registry
        ModelsTable = new Table(this, "ModelsTable", new TableProps
        {
            TableName = "snout-spotter-models",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "model_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY,
        });

        ModelsTable.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
        {
            IndexName = "by-type",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "model_type", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "created_at", Type = AttributeType.STRING },
            ProjectionType = ProjectionType.ALL
        });

        // DynamoDB table for pre-computed dashboard stats
        StatsTable = new Table(this, "StatsTable", new TableProps
        {
            TableName = "snout-spotter-stats",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "stat_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.DESTROY
        });

        _ = new StringParameter(this, "StatsTableNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/stats-table-name",
            StringValue = StatsTable.TableName
        });

        // DynamoDB table for pet profiles
        PetsTable = new Table(this, "PetsTable", new TableProps
        {
            TableName = "snout-spotter-pets",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "household_id", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "pet_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.RETAIN,
            PointInTimeRecovery = true
        });

        // DynamoDB table for user accounts (maps Okta sub to household memberships)
        UsersTable = new Table(this, "UsersTable", new TableProps
        {
            TableName = "snout-spotter-users",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "user_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.RETAIN,
            PointInTimeRecovery = true
        });

        // DynamoDB table for household metadata
        HouseholdsTable = new Table(this, "HouseholdsTable", new TableProps
        {
            TableName = "snout-spotter-households",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "household_id", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.RETAIN,
            PointInTimeRecovery = true
        });

        // Device registry — SnoutSpotter Pis, SPC devices, and Pi <-> SPC links.
        // SK uses three prefixes (snoutspotter#{thing}, spc#{spc_id},
        // link#spc#{spc_id}#snoutspotter#{thing}) so a single Query(PK=household)
        // returns every device + link row in one call.
        DevicesTable = new Table(this, "DevicesTable", new TableProps
        {
            TableName = "snout-spotter-devices",
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "household_id", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "sk", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            RemovalPolicy = RemovalPolicy.RETAIN,
            PointInTimeRecovery = true
        });

        // ECR repository for Training Agent Docker image
        TrainingAgentEcrRepo = new Repository(this, "TrainingAgentEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-training-agent",
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

        // ECR repository for UpdateTrainingProgress Lambda
        UpdateTrainingProgressEcrRepo = new Repository(this, "UpdateTrainingProgressEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-update-training-progress",
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

        // ECR repository for StatsRefresh Lambda
        StatsRefreshEcrRepo = new Repository(this, "StatsRefreshEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-stats-refresh",
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

        // ECR repository for SPC (Sure Pet Care) connector Lambda
        SpcEcrRepo = new Repository(this, "SpcEcrRepo", new RepositoryProps
        {
            RepositoryName = "snout-spotter-spc",
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

        _ = new StringParameter(this, "HouseholdsTableNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/households-table-name",
            StringValue = HouseholdsTable.TableName
        });

        _ = new StringParameter(this, "PetsTableNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/pets-table-name",
            StringValue = PetsTable.TableName
        });

        _ = new StringParameter(this, "UsersTableNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/users-table-name",
            StringValue = UsersTable.TableName
        });

        _ = new StringParameter(this, "DevicesTableNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/devices-table-name",
            StringValue = DevicesTable.TableName
        });

        // Outputs
        _ = new CfnOutput(this, "ApiEcrRepoUri", new CfnOutputProps
        {
            Value = ApiEcrRepo.RepositoryUri,
            Description = "ECR repository URI for the API"
        });

        _ = new StringParameter(this, "DataBucketNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/data-bucket-name",
            StringValue = DataBucket.BucketName
        });

        _ = new StringParameter(this, "SettingsTableNameParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/core/settings-table-name",
            StringValue = SettingsTable.TableName
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
