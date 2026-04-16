namespace SnoutSpotter.Api.Models;

public record DashboardStats(
    int TotalClips,
    int ClipsToday,
    int TotalDetections,
    int MyDogDetections,
    int KnownPetDetections,
    Dictionary<string, int>? PetDetectionCounts,
    string? LastUploadTime,
    int PiOnlineCount,
    int PiTotalCount,
    string? RefreshedAt = null);