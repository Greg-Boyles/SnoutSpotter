using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IHouseholdService
{
    Task<HouseholdInfo> CreateAsync(string name, string ownerUserId);
    Task<HouseholdInfo?> GetByIdAsync(string householdId);
    Task<List<HouseholdInfo>> GetForUserAsync(UserProfile user);
}
