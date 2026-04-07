namespace SnoutSpotter.Contracts;

/// <summary>
/// SQS message for the rerun-inference queue.
/// Produced by API (LabelsController), consumed by RunInference Lambda.
/// Queue: snout-spotter-rerun-inference
/// </summary>
public record InferenceMessage(string ClipId);

/// <summary>
/// SQS message for the backfill-boxes queue.
/// Produced by API (LabelService), consumed by AutoLabel Lambda.
/// Queue: snout-spotter-backfill-boxes
/// </summary>
public record BackfillMessage(List<string> KeyframeKeys);
