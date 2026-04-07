namespace SnoutSpotter.Contracts;

/// <summary>
/// SQS message for the rerun-inference queue.
/// Produced by API (LabelsController), consumed by RunInference Lambda.
/// Queue: snout-spotter-rerun-inference
/// </summary>
public record InferenceMessage(string ClipId);