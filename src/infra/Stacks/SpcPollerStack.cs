using Amazon.CDK;
using Amazon.CDK.AWS.ECR;
using Amazon.CDK.AWS.IAM;
using Amazon.CDK.AWS.Lambda;
using Amazon.CDK.AWS.Lambda.EventSources;
using Amazon.CDK.AWS.SQS;
using Amazon.CDK.AWS.SSM;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class SpcPollerStackProps : StackProps
{
    public required Repository SpcPollerEcrRepo { get; init; }
    public required string ImageTag { get; init; }
}

public class SpcPollerStack : Stack
{
    public Queue BurstQueue { get; }

    public SpcPollerStack(Construct scope, string id, SpcPollerStackProps props) : base(scope, id, props)
    {
        // DLQ for burst messages that fail repeatedly (likely a malformed body
        // or a token-expired race that keeps throwing rather than returning).
        var dlq = new Queue(this, "SpcBurstDlq", new QueueProps
        {
            QueueName = "snout-spotter-spc-burst-dlq",
            RetentionPeriod = Duration.Days(7)
        });

        // Motion triggers a message here; the Lambda self-enqueues a 30s-delayed
        // `continue` message while the burst window is open, then lets the chain die.
        BurstQueue = new Queue(this, "SpcBurstQueue", new QueueProps
        {
            QueueName = "snout-spotter-spc-burst",
            VisibilityTimeout = Duration.Seconds(90), // > Lambda timeout
            RetentionPeriod = Duration.Days(1),
            DeadLetterQueue = new DeadLetterQueue
            {
                Queue = dlq,
                MaxReceiveCount = 3
            }
        });

        // Read table names from SSM — written by CoreStack, no CDK cross-stack dep.
        var householdsTable = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/households-table-name");
        var petsTable = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/pets-table-name");
        var eventsTable = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/spc-events-table-name");
        var burstStateTable = StringParameter.ValueForStringParameter(this, "/snoutspotter/core/spc-burst-state-table-name");

        var pollerFunction = new DockerImageFunction(this, "SpcPollerFunction", new DockerImageFunctionProps
        {
            FunctionName = "snout-spotter-spc-poller",
            Description = "Motion-triggered burst poller for Sure Pet Care timeline events",
            Code = DockerImageCode.FromEcr(props.SpcPollerEcrRepo, new EcrImageCodeProps
            {
                TagOrDigest = props.ImageTag
            }),
            MemorySize = 256,
            Timeout = Duration.Seconds(60),
            Environment = new Dictionary<string, string>
            {
                ["HOUSEHOLDS_TABLE"] = householdsTable,
                ["PETS_TABLE"] = petsTable,
                ["SPC_EVENTS_TABLE"] = eventsTable,
                ["SPC_BURST_STATE_TABLE"] = burstStateTable,
                ["SPC_BURST_QUEUE_URL"] = BurstQueue.QueueUrl,
                ["SPC_BASE_URL"] = "https://app-api.beta.surehub.io"
            }
        });

        // IAM — all scoped by table name / secret prefix. We don't use table refs
        // because the tables live in CoreStack and we want no cross-stack CDK dep.
        pollerFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "secretsmanager:GetSecretValue" },
            Resources = new[] { $"arn:aws:secretsmanager:{Region}:{Account}:secret:snoutspotter/spc/*" }
        }));

        // Households: read for spc_integration.spc_household_id, update for
        // MarkTokenExpired + SetLastSyncAt.
        pollerFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:GetItem", "dynamodb:UpdateItem" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-households" }
        }));

        // Pets: Query to build spc_pet_id -> pet_id map.
        pollerFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:Query" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-pets" }
        }));

        // Events: PutItem (and Query for future UI reads if we ever run them here).
        pollerFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:PutItem", "dynamodb:Query" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-spc-events" }
        }));

        // Burst state: Get for read, Update for extend / cursor / last_poll_at.
        pollerFunction.AddToRolePolicy(new PolicyStatement(new PolicyStatementProps
        {
            Effect = Effect.ALLOW,
            Actions = new[] { "dynamodb:GetItem", "dynamodb:UpdateItem" },
            Resources = new[] { $"arn:aws:dynamodb:{Region}:{Account}:table/snout-spotter-spc-burst-state" }
        }));

        // SQS — receive/delete handled by the event source grant; SendMessage is
        // needed for self-scheduling continue messages.
        BurstQueue.GrantSendMessages(pollerFunction);

        pollerFunction.AddEventSource(new SqsEventSource(BurstQueue, new SqsEventSourceProps
        {
            // BatchSize = 1 — each message is per-household and the handler
            // doesn't benefit from batching. Keeps accounting simple.
            BatchSize = 1
        }));

        // SSM — IngestStack reads this to SendMessage on motion without a CDK dep.
        _ = new StringParameter(this, "SpcBurstQueueUrlParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/spc-poller/burst-queue-url",
            StringValue = BurstQueue.QueueUrl
        });
        _ = new StringParameter(this, "SpcBurstQueueArnParam", new StringParameterProps
        {
            ParameterName = "/snoutspotter/spc-poller/burst-queue-arn",
            StringValue = BurstQueue.QueueArn
        });

        _ = new CfnOutput(this, "SpcBurstQueueUrl", new CfnOutputProps
        {
            Value = BurstQueue.QueueUrl,
            Description = "Motion-triggered SPC burst queue"
        });
    }
}
