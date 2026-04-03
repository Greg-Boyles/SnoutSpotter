namespace SnoutSpotter.Api.Models;

public record ClipSummary(
    string ClipId,
    string S3Key,
    long Timestamp,
    int DurationSeconds,
    string Date,
    string? Device,
    int KeyframeCount,
    string DetectionType,
    int DetectionCount,
    string CreatedAt)
{
    public string? ThumbnailUrl { get; init; }
}