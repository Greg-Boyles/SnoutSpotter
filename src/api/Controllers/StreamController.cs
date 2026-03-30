using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Services;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StreamController : ControllerBase
{
    private readonly StreamService _streamService;
    private readonly PiUpdateService _piUpdateService;

    public StreamController(StreamService streamService, PiUpdateService piUpdateService)
    {
        _streamService = streamService;
        _piUpdateService = piUpdateService;
    }

    [HttpPost("{thingName}/start")]
    public async Task<ActionResult> StartStream(string thingName)
    {
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
