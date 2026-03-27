namespace SnoutSpotter.Api.Models;

public record ClipSummary(
    string ClipId,
    string S3Key,
    long Timestamp,
    int DurationSeconds,
    string Date,
    int KeyframeCount,
    string DetectionType,
    int DetectionCount,
    string CreatedAt)
{
    public string? ThumbnailUrl { get; init; }
}

public record ClipDetail(
    string ClipId,
    string S3Key,
    long Timestamp,
    int DurationSeconds,
    string Date,
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

public record ClipListResponse(
    List<ClipSummary> Clips,
    string? NextPageKey,
    int TotalCount);

public record DetectionSummary(
    string ClipId,
    string DetectionType,
    int DetectionCount,
    long Timestamp,
    string Date,
    string? FirstKeyframeKey);

public record DashboardStats(
    int TotalClips,
    int ClipsToday,
    int TotalDetections,
    int MyDogDetections,
    string? LastUploadTime,
    bool PiOnline);

public record PresignedUrlResponse(string Url, int ExpiresInSeconds);
