namespace SnoutSpotter.Api.Models;

public record ClipDetail(
    string ClipId,
    string S3Key,
    long Timestamp,
    int DurationSeconds,
    string Date,
    string? Device,
    int KeyframeCount,
    List<string> KeyframeKeys,
    string DetectionType,
    int DetectionCount,
    string? Detections,
    bool Labeled,
    string CreatedAt,
    string? InferenceAt)
{
    public string? VideoUrl { get; init; }
    public List<string>? KeyframeUrls { get; init; }
}