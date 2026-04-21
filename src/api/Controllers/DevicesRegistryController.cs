using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

// Registry for both SnoutSpotter Pi cameras and linked Sure Pet Care devices.
// Lives at api/devices (plural). Not to be confused with api/device (singular),
// which is the live IoT-shadow + OTA surface.
[ApiController]
[Route("api/devices")]
[Authorize]
public class DevicesRegistryController : ControllerBase
{
    private readonly IDeviceRegistryService _registry;
    private readonly ILogger<DevicesRegistryController> _log;

    public DevicesRegistryController(IDeviceRegistryService registry, ILogger<DevicesRegistryController> log)
    {
        _registry = registry;
        _log = log;
    }

    [HttpGet]
    public async Task<ActionResult<DeviceListResponse>> List()
    {
        var hh = HttpContext.GetHouseholdId();
        return await _registry.ListAsync(hh);
    }

    [HttpPut("snoutspotter/{thingName}")]
    public async Task<ActionResult<SnoutSpotterDeviceDto>> UpdateSnoutspotter(string thingName, [FromBody] UpdateDeviceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest(new { error = "display_name_required" });

        var hh = HttpContext.GetHouseholdId();
        try
        {
            return await _registry.UpdateSnoutSpotterAsync(hh, thingName, req);
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound(new { error = "device_not_in_household" });
        }
    }

    [HttpPut("spc/{spcDeviceId}")]
    public async Task<ActionResult<SpcDeviceDto>> UpdateSpc(string spcDeviceId, [FromBody] UpdateDeviceRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DisplayName))
            return BadRequest(new { error = "display_name_required" });

        var hh = HttpContext.GetHouseholdId();
        return await _registry.UpdateSpcAsync(hh, spcDeviceId, req);
    }

    [HttpPost("spc/{spcDeviceId}/refresh")]
    public async Task<ActionResult<SpcDeviceDto>> RefreshSpc(string spcDeviceId)
    {
        var hh = HttpContext.GetHouseholdId();
        try
        {
            return await _registry.RefreshSpcFromSpcAsync(hh, spcDeviceId);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = "refresh_failed", message = ex.Message });
        }
    }

    [HttpPost("links")]
    public async Task<ActionResult<DeviceLinkDto>> Link([FromBody] CreateLinkRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.SpcDeviceId) || string.IsNullOrWhiteSpace(req.SnoutspotterThingName))
            return BadRequest(new { error = "spc_device_id_and_thing_name_required" });

        var hh = HttpContext.GetHouseholdId();
        try
        {
            return await _registry.LinkAsync(hh, req.SpcDeviceId, req.SnoutspotterThingName);
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound(new { error = "device_not_in_household" });
        }
    }

    [HttpDelete("links/{spcDeviceId}/{thingName}")]
    public async Task<IActionResult> Unlink(string spcDeviceId, string thingName)
    {
        var hh = HttpContext.GetHouseholdId();
        await _registry.UnlinkAsync(hh, spcDeviceId, thingName);
        return NoContent();
    }
}
