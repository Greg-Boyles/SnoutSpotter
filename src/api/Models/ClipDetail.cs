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
    public List<KeyframeDetectionDto>? KeyframeDetections { get; init; }
}

public record KeyframeDetectionDto(
    string KeyframeKey,
    string Label,
    List<DetectionBoxDto> Detections);

public record DetectionBoxDto(
    string Label,
    float Confidence,
    BoundingBoxDto BoundingBox);

public record BoundingBoxDto(
    float X, float Y, float Width, float Height);