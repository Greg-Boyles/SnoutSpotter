namespace SnoutSpotter.Lambda.Spc.Models;

public record UserProfile(
    string UserId,
    string? Email,
    string? Name,
    List<HouseholdMembership> Households);

public record HouseholdMembership(
    string HouseholdId,
    string Role,
    string JoinedAt);
