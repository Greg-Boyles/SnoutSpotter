using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IStatsRefreshService
{
    Task<DashboardStats?> GetCachedDashboardStatsAsync();
    Task<object?> GetCachedActivityAsync();
    Task<object?> GetCachedLabelStatsAsync();
    void TriggerRefreshIfStale(string statId, string? refreshedAt);
}
