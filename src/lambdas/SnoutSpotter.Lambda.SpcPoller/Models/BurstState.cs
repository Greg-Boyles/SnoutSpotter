namespace SnoutSpotter.Lambda.SpcPoller.Models;

// In-memory view of snout-spotter-spc-burst-state row. `LastTimelineId == null`
// is the "seed mode" signal: first-ever motion for this household, we record
// the latest id and skip persisting history.
public record BurstState(
    string HouseholdId,
    DateTime PollUntil,
    long? LastTimelineId,
    string? LastPollAt);
