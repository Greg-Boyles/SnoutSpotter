using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services;

public interface IUserMembershipService
{
    Task<UserProfile?> GetByIdAsync(string userId);
}
