using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IClipService
{
    Task<ClipListResponse> GetClipsAsync(string? date = null, string? device = null, int limit = 20, string? nextPageKey = null);
    Task<ClipDetail?> GetClipByIdAsync(string clipId);
    Task<List<DetectionSummary>> GetDetectionsAsync(string? detectionType = null, string? dateFrom = null, string? dateTo = null, int limit = 50);
    Task<int> GetClipCountForDateAsync(string date);
}
