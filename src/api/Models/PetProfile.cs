namespace SnoutSpotter.Api.Models;

public record PetProfile(
    string HouseholdId,
    string PetId,
    string Name,
    string? Breed,
    string? PhotoUrl,
    string CreatedAt);

public record CreatePetRequest(string Name, string? Breed = null);

public record UpdatePetRequest(string Name, string? Breed = null);
