using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SQS;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class AutoLabelStackProps : StackProps
{
    public required Repository AutoLabelEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required Bucket DataBucket { get; init; }
    public required Table LabelsTable { get; init; }
}

public class AutoLabelStack : Stack
{
    public Function AutoLabelFunction { get; }
    public Queue BackfillQueue { get; }

    public AutoLabelStack(Construct scope, string id, AutoLabelStackProps props) : base(scope, id, props)
    {
        // Dead-letter queue for failed backfill batches
        var dlq = new Queue(this, "BackfillDlq", new QueueProps
        {
            QueueName = "snout-spotter-backfill-boxes-dlq",
            RetentionPeriod = Duration.Days(7)
        });

        // Backfill queue — feeds bounding-box reprocess jobs to the Lambda one at a time
        BackfillQueue = new Queue(this, "BackfillQueue", new QueueProps
        {
            QueueName = "snout-spotter-backfill-boxes",
            VisibilityTimeout = Duration.Minutes(20), // must exceed Lambda timeout
            RetentionPeriod = Duration.Days(1),
            DeadLetterQueue = new DeadLetterQueue
            {
                Queue = dlq,
                MaxReceiveCount = 2
            }
        });

        AutoLabelFunction = new DockerImageFunction(this, "AutoLabelFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-auto-label",
            Description = "Auto-labels keyframes using pre-trained YOLOv8 dog detection",
            Code = DockerImageCode.FromEcr(props.AutoLabelEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 2048,
            Timeout = Duration.Minutes(15),
            Environment = new Dictionary<string, string>
            {
                ["BUCKET_NAME"] = props.DataBucket.BucketName,
                ["LABELS_TABLE"] = props.LabelsTable.TableName,
                ["MODEL_KEY"] = "models/yolov8n.onnx"
            }
        });

        props.DataBucket.GrantRead(AutoLabelFunction);
        props.LabelsTable.GrantReadWriteData(AutoLabelFunction);

        // SQS event source — MaxConcurrency=1 ensures only one Lambda runs at a time
        AutoLabelFunction.AddEventSource(new SqsEventSource(BackfillQueue, new SqsEventSourceProps
        {
            BatchSize = 1,
            MaxConcurrency = 1
        }));

        _ = new CfnOutput(this, "AutoLabelFunctionArn", new CfnOutputProps
        {
            Value = AutoLabelFunction.FunctionArn,
            Description = "ARN of the AutoLabel Lambda function"
        });

        _ = new CfnOutput(this, "BackfillQueueUrl", new CfnOutputProps
        {
            Value = BackfillQueue.QueueUrl,
            Description = "SQS queue URL for backfill bounding box jobs"
        });
    }
}
