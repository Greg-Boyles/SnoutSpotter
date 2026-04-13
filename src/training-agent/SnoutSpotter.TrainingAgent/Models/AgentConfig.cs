namespace SnoutSpotter.TrainingAgent.Models;

public class AgentConfig
{
    public string AgentName { get; set; } = "";
    public IoTConfig Iot { get; set; } = new();
    public S3Config S3 { get; set; } = new();
    public CredentialsProviderConfig CredentialsProvider { get; set; } = new();
    public TrainingConfig? Training { get; set; }
}

public class TrainingConfig
{
    public string JobQueueUrl { get; set; } = "";
    public string JobsTable { get; set; } = "snout-spotter-training-jobs";
    public string ModelsTable { get; set; } = "snout-spotter-models";
}