using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services;

public interface IHouseholdIntegrationService
{
    Task<SpcIntegrationState?> GetAsync(string householdId, CancellationToken ct = default);
    Task SaveAsync(string householdId, SpcIntegrationState state, CancellationToken ct = default);
    Task MarkTokenExpiredAsync(string householdId, CancellationToken ct = default);
    Task ClearAsync(string householdId, CancellationToken ct = default);
}
