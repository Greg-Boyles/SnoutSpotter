using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

public class AgentReportedState
{
    [JsonPropertyName("agentType")]      
    public string AgentType { get; init; } = "training-agent";
    
    [JsonPropertyName("agentVersion")]   
    public string AgentVersion { get; init; } = "";
    
    [JsonPropertyName("hostname")]       
    public string Hostname { get; init; } = "";
    
    [JsonPropertyName("lastHeartbeat")]  
    public string LastHeartbeat { get; init; } = "";
    
    [JsonPropertyName("status")]         
    public string Status { get; init; } = "idle";
    
    [JsonPropertyName("updateStatus")]   
    public string UpdateStatus { get; init; } = "idle";

    [JsonPropertyName("gpu")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GpuStatus? Gpu { get; init; }

    [JsonPropertyName("deferredVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeferredVersion { get; init; }

    [JsonPropertyName("deferReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DeferReason { get; init; }
}
