using SnoutSpotter.Spc.Client.Models;

namespace SnoutSpotter.Spc.Client.Services.Interfaces;

public interface ISpcApiClient
{
    Task<SpcLoginData> LoginAsync(string email, string password, string clientUid, CancellationToken ct = default);
    Task<List<SpcHouseholdResource>> ListHouseholdsAsync(string accessToken, CancellationToken ct = default);
    Task<List<SpcPetResource>> ListPetsAsync(string accessToken, long spcHouseholdId, CancellationToken ct = default);
    Task<List<SpcDeviceResource>> ListDevicesAsync(string accessToken, long spcHouseholdId, CancellationToken ct = default);
    Task<List<SpcTimelineResource>> ListTimelineAsync(string accessToken, long spcHouseholdId, long? sinceId, int pageSize, CancellationToken ct = default);
}
