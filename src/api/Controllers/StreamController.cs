using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StreamController : ControllerBase
{
    private readonly IStreamService _streamService;
    private readonly IPiUpdateService _piUpdateService;
    private readonly IDeviceOwnershipService _ownership;

    public StreamController(IStreamService streamService, IPiUpdateService piUpdateService, IDeviceOwnershipService ownership)
    {
        _streamService = streamService;
        _piUpdateService = piUpdateService;
        _ownership = ownership;
    }

    [HttpPost("{thingName}/start")]
    public async Task<ActionResult> StartStream(string thingName)
    {
        if (!await _ownership.DeviceBelongsToHouseholdAsync(thingName, HttpContext.GetHouseholdId()))
            return Forbid();
        try
        {
            var result = await _streamService.StartStreamAsync(thingName);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{thingName}/hls")]
    public async Task<ActionResult> GetHlsUrl(string thingName)
    {
        if (!await _ownership.DeviceBelongsToHouseholdAsync(thingName, HttpContext.GetHouseholdId()))
            return Forbid();
        try
        {
            var url = await _streamService.GetHlsUrlAsync(thingName);
            if (url == null)
                return NotFound(new { error = "Stream not available yet — Pi may still be starting" });
            return Ok(new { hlsUrl = url });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("{thingName}/stop")]
    public async Task<ActionResult> StopStream(string thingName)
    {
        if (!await _ownership.DeviceBelongsToHouseholdAsync(thingName, HttpContext.GetHouseholdId()))
            return Forbid();
        try
        {
            await _streamService.StopStreamAsync(thingName);
            return Ok(new { message = "Stream stop requested", thingName });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{thingName}/status")]
    public async Task<ActionResult> GetStreamStatus(string thingName)
    {
        if (!await _ownership.DeviceBelongsToHouseholdAsync(thingName, HttpContext.GetHouseholdId()))
            return Forbid();
        var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
        if (shadow == null)
            return NotFound(new { error = "Device not found" });

        return Ok(new
        {
            thingName,
            streaming = shadow.Streaming ?? false,
            streamError = shadow.StreamError
        });
    }
}
