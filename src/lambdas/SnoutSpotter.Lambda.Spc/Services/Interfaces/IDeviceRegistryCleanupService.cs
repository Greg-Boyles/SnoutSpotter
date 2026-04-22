namespace SnoutSpotter.Lambda.Spc.Services.Interfaces;

public interface IDeviceRegistryCleanupService
{
    // Called from DELETE /api/integrations/spc. Removes every spc#<id> row and
    // every link#spc#<id>#snoutspotter#<thing> row for the household. The
    // snoutspotter# rows are intentionally preserved — our cameras still exist
    // even if the SPC account is unlinked.
    Task ClearSpcDevicesAndLinksAsync(string householdId, CancellationToken ct = default);
}
