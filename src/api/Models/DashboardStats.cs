namespace SnoutSpotter.Api.Models;

public record DashboardStats(
    int TotalClips,
    int ClipsToday,
    int TotalDetections,
    int MyDogDetections,
    string? LastUploadTime,
    bool PiOnline);