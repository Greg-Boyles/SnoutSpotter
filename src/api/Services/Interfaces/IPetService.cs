using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IPetService
{
    Task<List<PetProfile>> ListAsync(string householdId = "default");
    Task<PetProfile?> GetAsync(string petId, string householdId = "default");
    Task<PetProfile> CreateAsync(CreatePetRequest request, string householdId = "default");
    Task<PetProfile> UpdateAsync(string petId, UpdatePetRequest request, string householdId = "default");
    Task DeleteAsync(string petId, string householdId = "default");
    Task<bool> ExistsAsync(string petId, string householdId = "default");
    Task<MigrationResult> MigrateLegacyLabelsAsync(string petId);
}

public record MigrationResult(int LabelsUpdated, int ClipsUpdated);
