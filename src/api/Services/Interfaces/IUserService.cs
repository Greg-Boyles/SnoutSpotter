using System.Security.Claims;
using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IUserService
{
    Task<UserProfile> GetOrCreateAsync(string userId, ClaimsPrincipal? claims);
    Task<UserProfile?> GetByIdAsync(string userId);
    void InvalidateCache(string userId);
}
