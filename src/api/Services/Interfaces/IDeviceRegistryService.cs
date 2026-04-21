using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IDeviceRegistryService
{
    Task<DeviceListResponse> ListAsync(string householdId);
    Task<SnoutSpotterDeviceDto> UpdateSnoutSpotterAsync(string householdId, string thingName, UpdateDeviceRequest req);
    Task<SpcDeviceDto> UpdateSpcAsync(string householdId, string spcDeviceId, UpdateDeviceRequest req);
    Task<SpcDeviceDto> RefreshSpcFromSpcAsync(string householdId, string spcDeviceId);
    Task<DeviceLinkDto> LinkAsync(string householdId, string spcDeviceId, string thingName);
    Task UnlinkAsync(string householdId, string spcDeviceId, string thingName);
}
