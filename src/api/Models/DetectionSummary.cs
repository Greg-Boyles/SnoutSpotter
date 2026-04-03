namespace SnoutSpotter.Api.Models;

public record DetectionSummary(
    string ClipId,
    string DetectionType,
    int DetectionCount,
    long Timestamp,
    string Date,
    string? Device,
    string? FirstKeyframeKey);