namespace SnoutSpotter.TrainingAgent.Models;

public class AgentConfig
{
    public string AgentName { get; set; } = "";
    public IoTConfig IoT { get; set; } = new();
    public S3Config S3 { get; set; } = new();
    public CredentialsProviderConfig CredentialsProvider { get; set; } = new();
}