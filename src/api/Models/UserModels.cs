namespace SnoutSpotter.Api.Models;

public record UserProfile(
    string UserId,
    string? Email,
    string? Name,
    List<HouseholdMembership> Households,
    string CreatedAt,
    string? LastLoginAt);

public record HouseholdMembership(
    string HouseholdId,
    string Role,
    string JoinedAt);

public record HouseholdInfo(
    string HouseholdId,
    string Name,
    string CreatedAt);

public record CreateHouseholdRequest(string Name);
