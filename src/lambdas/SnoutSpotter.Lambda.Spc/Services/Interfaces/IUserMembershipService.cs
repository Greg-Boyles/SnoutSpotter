using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services.Interfaces;

public interface IUserMembershipService
{
    Task<UserProfile?> GetByIdAsync(string userId);
}
