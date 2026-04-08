using Amazon.CDK;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AWS.SSM;
using Constructs;
using IoT = Amazon.CDK.AWS.IoT;

namespace SnoutSpotter.Infra.Stacks;

public class TrainingProgressStackProps : StackProps
{
    public required Repository UpdateTrainingProgressEcrRepo { get; init; }
    public required string ImageTag { get; init; }
    public required Table TrainingJobsTable { get; init; }
}

public class TrainingProgressStack : Stack
{
    public TrainingProgressStack(Construct scope, string id, TrainingProgressStackProps props) : base(scope, id, props)
    {
        var progressFunction = new DockerImageFunction(this, "UpdateTrainingProgressFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-update-training-progress",
            Description = "Updates training job progress in DynamoDB from MQTT messages",
            Code = DockerImageCode.FromEcr(props.UpdateTrainingProgressEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 256,
            Timeout = Duration.Seconds(10),
            Environment = new Dictionary<string, string>
            {
                ["TRAINING_JOBS_TABLE"] = props.TrainingJobsTable.TableName
            }
        });

        props.TrainingJobsTable.GrantWriteData(progressFunction);

        // Allow IoT Rule to invoke the Lambda
        progressFunction.AddPermission("IoTRuleInvoke", new Permission
        {
            Principal = new ServicePrincipal("iot.amazonaws.com"),
            SourceArn = $"arn:aws:iot:{Region}:{Account}:rule/snoutspotter_trainer_progress"
        });

        // IoT Topic Rule: routes training progress messages to Lambda
        _ = new IoT.CfnTopicRule(this, "TrainerProgressRule", new IoT.CfnTopicRuleProps
        {
            RuleName = "snoutspotter_trainer_progress",
            TopicRulePayload = new IoT.CfnTopicRule.TopicRulePayloadProperty
            {
                Sql = "SELECT * FROM 'snoutspotter/trainer/+/progress'",
                AwsIotSqlVersion = "2016-03-23",
                Actions = new[]
                {
                    new IoT.CfnTopicRule.ActionProperty
                    {
                        Lambda = new IoT.CfnTopicRule.LambdaActionProperty
                        {
                            FunctionArn = progressFunction.FunctionArn
                        }
                    }
                },
                RuleDisabled = false
            }
        });

        // SQS queue for training job dispatch (API produces, training agents consume)
        var jobDlq = new Queue(this, "TrainingJobDlq", new QueueProps
        {
            QueueName = "snout-spotter-training-jobs-dlq",
            RetentionPeriod = Duration.Days(7)
        });

        var jobQueue = new Queue(this, "TrainingJobQueue", new QueueProps
        {
            QueueName = "snout-spotter-training-jobs-queue",
            VisibilityTimeout = Duration.Hours(12),
            RetentionPeriod = Duration.Days(3),
            DeadLetterQueue = new DeadLetterQueue
            {
                Queue = jobDlq,
                MaxReceiveCount = 2
            }
        });

        _ = new StringParameter(this, "TrainingJobQueueUrlParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/training/job-queue-url",
            StringValue = jobQueue.QueueUrl
        });
    }
}
