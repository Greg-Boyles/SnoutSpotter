namespace SnoutSpotter.Lambda.PiMgmt.Services;

public interface IDeviceProvisioningService
{
    Task<DeviceRegistrationResult> RegisterAsync(string thingName);
    Task DeregisterAsync(string thingName);
    Task<List<string>> ListDevicesAsync();

    Task<DeviceRegistrationResult> RegisterTrainerAsync(string thingName);
    Task DeregisterTrainerAsync(string thingName);
    Task<List<string>> ListTrainersAsync();
}
