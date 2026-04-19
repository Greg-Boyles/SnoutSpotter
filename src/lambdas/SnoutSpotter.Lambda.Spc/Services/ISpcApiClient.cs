using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services;

public interface ISpcApiClient
{
    Task<SpcLoginData> LoginAsync(string email, string password, string clientUid, CancellationToken ct = default);
    Task<List<SpcHouseholdResource>> ListHouseholdsAsync(string accessToken, CancellationToken ct = default);
    Task<List<SpcPetResource>> ListPetsAsync(string accessToken, long spcHouseholdId, CancellationToken ct = default);
    Task<List<SpcDeviceResource>> ListDevicesAsync(string accessToken, long spcHouseholdId, CancellationToken ct = default);
}
