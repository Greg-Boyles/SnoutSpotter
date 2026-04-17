using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DetectionsController : ControllerBase
{
    private readonly IClipService _clipService;

    public DetectionsController(IClipService clipService)
    {
        _clipService = clipService;
    }

    [HttpGet]
    public async Task<ActionResult<List<DetectionSummary>>> GetDetections(
        [FromQuery] string? type = null,
        [FromQuery] string? dateFrom = null,
        [FromQuery] string? dateTo = null,
        [FromQuery] int limit = 50)
    {
        var detections = await _clipService.GetDetectionsAsync(HttpContext.GetHouseholdId(), type, dateFrom, dateTo, limit);
        return Ok(detections);
    }
}
