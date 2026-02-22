using Amazon.CDK;
using Amazon.CDK.AWS.Budgets;
using Amazon.CDK.AWS.CloudWatch;
using Amazon.CDK.AWS.CloudWatch.Actions;
using Amazon.CDK.AWS.S3;
using Amazon.CDK.AWS.SNS;
using Amazon.CDK.AWS.SNS.Subscriptions;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class MonitoringStackProps : StackProps
{
    public required Bucket DataBucket { get; init; }
}

public class MonitoringStack : Stack
{
    public MonitoringStack(Construct scope, string id, MonitoringStackProps props) : base(scope, id, props)
    {
        // SNS topic for alerts
        var alertTopic = new Topic(this, "AlertTopic", new TopicProps
        {
            TopicName = "snout-spotter-alerts"
        });

        // Pi heartbeat alarm: triggers if no heartbeat received for 15 minutes
        var piHeartbeatAlarm = new Alarm(this, "PiHeartbeatAlarm", new AlarmProps
        {
            AlarmName = "SnoutSpotter-PiOffline",
            AlarmDescription = "Pi Zero has not sent a heartbeat in 15 minutes",
            Metric = new Metric(new MetricProps
            {
                Namespace = "SnoutSpotter",
                MetricName = "PiHeartbeat",
                Statistic = "Sum",
                Period = Duration.Minutes(5)
            }),
            EvaluationPeriods = 3,
            Threshold = 1,
            ComparisonOperator = ComparisonOperator.LESS_THAN_THRESHOLD,
            TreatMissingData = TreatMissingData.BREACHING
        });

        piHeartbeatAlarm.AddAlarmAction(new SnsAction(alertTopic));

        // Budget alert at $10/month
        _ = new CfnBudget(this, "MonthlyBudget", new CfnBudgetProps
        {
            Budget = new CfnBudget.BudgetDataProperty
            {
                BudgetName = "SnoutSpotter-MonthlyBudget",
                BudgetType = "COST",
                TimeUnit = "MONTHLY",
                BudgetLimit = new CfnBudget.SpendProperty
                {
                    Amount = 10,
                    Unit = "USD"
                }
            },
            NotificationsWithSubscribers = new[]
            {
                new CfnBudget.NotificationWithSubscribersProperty
                {
                    Notification = new CfnBudget.NotificationProperty
                    {
                        ComparisonOperator = "GREATER_THAN",
                        NotificationType = "ACTUAL",
                        Threshold = 80,
                        ThresholdType = "PERCENTAGE"
                    },
                    Subscribers = new[]
                    {
                        new CfnBudget.SubscriberProperty
                        {
                            SubscriptionType = "SNS",
                            Address = alertTopic.TopicArn
                        }
                    }
                }
            }
        });

        // Output
        _ = new CfnOutput(this, "AlertTopicArn", new CfnOutputProps
        {
            Value = alertTopic.TopicArn,
            Description = "SNS topic for alerts - subscribe your email here"
        });
    }
}
