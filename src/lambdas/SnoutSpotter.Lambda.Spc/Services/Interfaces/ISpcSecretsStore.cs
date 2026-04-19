using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services.Interfaces;

public interface ISpcSecretsStore
{
    Task<string> SaveAsync(string householdId, SpcSecret secret, CancellationToken ct = default);
    Task<SpcSecret?> GetAsync(string householdId, CancellationToken ct = default);
    Task DeleteAsync(string householdId, CancellationToken ct = default);
}
