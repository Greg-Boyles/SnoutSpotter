using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/device")]
[Authorize]
public class DeviceUpdatesController : ControllerBase
{
    private readonly IPiUpdateService _piUpdateService;
    private readonly IDeviceOwnershipService _ownership;

    public DeviceUpdatesController(IPiUpdateService piUpdateService, IDeviceOwnershipService ownership)
    {
        _piUpdateService = piUpdateService;
        _ownership = ownership;
    }

    [HttpPost("{thingName}/update")]
    public async Task<ActionResult> TriggerUpdate(string thingName, [FromBody] UpdateRequest? request = null)
    {
        if (!await _ownership.DeviceBelongsToHouseholdAsync(thingName, HttpContext.GetHouseholdId()))
            return Forbid();
        try
        {
            await _piUpdateService.TriggerUpdateAsync(thingName, request?.Version);
            return Ok(new { message = "Update triggered", thingName, version = request?.Version ?? await _piUpdateService.GetLatestVersionAsync() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("update-all")]
    public async Task<ActionResult> TriggerUpdateAll([FromBody] UpdateRequest? request = null)
    {
        try
        {
            var hhId = HttpContext.GetHouseholdId();
            await _piUpdateService.TriggerUpdateAllAsync(hhId, request?.Version);
            var devices = await _piUpdateService.ListPisAsync(hhId);
            return Ok(new { message = "Update triggered for all devices", deviceCount = devices.Count, version = request?.Version ?? await _piUpdateService.GetLatestVersionAsync() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("releases")]
    public async Task<ActionResult> ListReleases()
    {
        var releases = await _piUpdateService.ListReleasesAsync();
        var latest = await _piUpdateService.GetLatestVersionAsync();
        return Ok(new { releases, latestVersion = latest });
    }

    [HttpDelete("releases/{version}")]
    public async Task<ActionResult> DeleteRelease(string version)
    {
        try
        {
            await _piUpdateService.DeleteReleaseAsync(version);
            return Ok(new { message = $"Release {version} deleted" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}
