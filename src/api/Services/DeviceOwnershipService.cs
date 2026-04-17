using System.Collections.Concurrent;
using Amazon.IoT;
using Amazon.IoT.Model;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class DeviceOwnershipService : IDeviceOwnershipService
{
    private readonly IAmazonIoT _iot;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (string? HouseholdId, DateTime Expiry)> Cache = new();

    public DeviceOwnershipService(IAmazonIoT iot)
    {
        _iot = iot;
    }

    public async Task<string?> GetHouseholdForDeviceAsync(string thingName)
    {
        if (Cache.TryGetValue(thingName, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.HouseholdId;

        try
        {
            var response = await _iot.DescribeThingAsync(new DescribeThingRequest
            {
                ThingName = thingName
            });

            var householdId = response.Attributes?.GetValueOrDefault("household_id");
            Cache[thingName] = (householdId, DateTime.UtcNow.Add(CacheTtl));
            return householdId;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    public async Task<bool> DeviceBelongsToHouseholdAsync(string thingName, string householdId)
    {
        var deviceHousehold = await GetHouseholdForDeviceAsync(thingName);
        return deviceHousehold == householdId;
    }

    public async Task SetDeviceHouseholdAsync(string thingName, string householdId)
    {
        await _iot.UpdateThingAsync(new UpdateThingRequest
        {
            ThingName = thingName,
            AttributePayload = new AttributePayload
            {
                Attributes = new Dictionary<string, string>
                {
                    ["household_id"] = householdId
                },
                Merge = true
            }
        });

        Cache[thingName] = (householdId, DateTime.UtcNow.Add(CacheTtl));
    }
}
