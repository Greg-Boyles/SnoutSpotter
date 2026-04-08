namespace SnoutSpotter.Contracts;

/// <summary>
/// SQS message for the backfill-boxes queue.
/// Produced by API (LabelService), consumed by AutoLabel Lambda.
/// Queue: snout-spotter-backfill-boxes
/// </summary>
public record BackfillMessage(List<string> KeyframeKeys);