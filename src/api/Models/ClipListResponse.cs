namespace SnoutSpotter.Api.Models;

public record ClipListResponse(
    List<ClipSummary> Clips,
    string? NextPageKey,
    int TotalCount);