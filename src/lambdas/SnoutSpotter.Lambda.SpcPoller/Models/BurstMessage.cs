using System.Text.Json.Serialization;

namespace SnoutSpotter.Lambda.SpcPoller.Models;

// Message body on snout-spotter-spc-burst queue. `kind` distinguishes a
// motion-triggered start/extend (from IngestClip) vs. a self-scheduled
// continue (from this Lambda).
public record BurstMessage(
    [property: JsonPropertyName("householdId")] string HouseholdId,
    [property: JsonPropertyName("kind")] string Kind);

public static class BurstMessageKinds
{
    public const string Motion = "motion";
    public const string Continue = "continue";
}
