using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IClipService
{
    Task<ClipListResponse> GetClipsAsync(string householdId, string? date = null, string? device = null, string? detectionType = null, int limit = 20, string? nextPageKey = null);
    Task<ClipDetail?> GetClipByIdAsync(string clipId);
    Task<List<DetectionSummary>> GetDetectionsAsync(string householdId, string? detectionType = null, string? dateFrom = null, string? dateTo = null, int limit = 50);
    Task<int> GetClipCountForDateAsync(string householdId, string date);
    Task DeleteClipAsync(string clipId);
    Task<List<string>> GetClipIdsForDateRangeAsync(string householdId, string? dateFrom, string? dateTo);
}
