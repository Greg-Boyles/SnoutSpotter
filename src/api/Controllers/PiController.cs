using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Services;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PiController : ControllerBase
{
    private readonly PiUpdateService _piUpdateService;
    private readonly HealthService _healthService;

    public PiController(PiUpdateService piUpdateService, HealthService healthService)
    {
        _piUpdateService = piUpdateService;
        _healthService = healthService;
    }

    [HttpGet("status")]
    public async Task<ActionResult> GetStatus()
    {
        var shadow = await _piUpdateService.GetPiShadowAsync();
        var latestVersion = await _piUpdateService.GetLatestVersionAsync();
        var piOnline = await _healthService.IsPiOnlineAsync();

        return Ok(new
        {
            piOnline,
            piVersion = shadow?.Version,
            piHostname = shadow?.Hostname,
            lastHeartbeat = shadow?.LastHeartbeat,
            updateStatus = shadow?.UpdateStatus ?? "idle",
            services = shadow?.Services,
            latestVersion,
            updateAvailable = latestVersion != null && shadow?.Version != null && latestVersion != shadow.Version
        });
    }

    [HttpPost("update")]
    public async Task<ActionResult> TriggerUpdate([FromBody] UpdateRequest? request = null)
    {
        try
        {
            await _piUpdateService.TriggerUpdateAsync(request?.Version);
            return Ok(new { message = "Update triggered", version = request?.Version ?? await _piUpdateService.GetLatestVersionAsync() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public record UpdateRequest(string? Version);
