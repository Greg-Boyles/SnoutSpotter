using SnoutSpotter.Spc.Client.Models;

namespace SnoutSpotter.Spc.Client.Services.Interfaces;

public interface ISpcSecretsStore
{
    Task<string> SaveAsync(string householdId, SpcSecret secret, CancellationToken ct = default);
    Task<SpcSecret?> GetAsync(string householdId, CancellationToken ct = default);
    Task DeleteAsync(string householdId, CancellationToken ct = default);
}
