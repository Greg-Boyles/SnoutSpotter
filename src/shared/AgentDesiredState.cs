using System.Text.Json.Serialization;

namespace SnoutSpotter.Shared.Training;

/// <summary>
/// Represents the desired state written to the agent's IoT shadow.
/// Used by the API when dispatching jobs/commands and by the agent when reading deltas.
/// Only one of the action properties will be set in any given update.
/// </summary>
public class AgentDesiredState
{
    [JsonPropertyName("trainingJob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TrainingJobDesired? TrainingJob { get; init; }

    [JsonPropertyName("cancelJob")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CancelJob { get; init; }

    [JsonPropertyName("agentVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AgentVersion { get; init; }

    [JsonPropertyName("forceUpdate")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ForceUpdate { get; init; }
}
