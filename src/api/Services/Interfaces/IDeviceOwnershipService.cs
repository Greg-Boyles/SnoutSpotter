namespace SnoutSpotter.Api.Services.Interfaces;

public interface IDeviceOwnershipService
{
    Task<string?> GetHouseholdForDeviceAsync(string thingName);
    Task<bool> DeviceBelongsToHouseholdAsync(string thingName, string householdId);
    Task SetDeviceHouseholdAsync(string thingName, string householdId);
}
