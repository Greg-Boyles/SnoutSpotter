namespace SnoutSpotter.Lambda.Spc.Services.Interfaces;

public interface IPetLinkService
{
    Task<List<string>> ListPetIdsAsync(string householdId, CancellationToken ct = default);
    Task<int> ApplyMappingsAsync(string householdId, IEnumerable<Models.PetMapping> mappings, CancellationToken ct = default);
    Task ClearAllAsync(string householdId, CancellationToken ct = default);
}
