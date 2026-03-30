using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Services;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PiController : ControllerBase
{
    private readonly PiUpdateService _piUpdateService;
    private readonly HealthService _healthService;
    private readonly LogService _logService;

    public PiController(PiUpdateService piUpdateService, HealthService healthService, LogService logService)
    {
        _piUpdateService = piUpdateService;
        _healthService = healthService;
        _logService = logService;
    }

    [HttpGet("devices")]
    public async Task<ActionResult> GetDevices()
    {
        var thingNames = await _piUpdateService.ListPisAsync();
        var latestVersion = await _piUpdateService.GetLatestVersionAsync();

        var devices = new List<object>();
        foreach (var thingName in thingNames)
        {
            var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
            var piOnline = shadow?.LastHeartbeat != null &&
                DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
                (DateTime.UtcNow - lastHb).TotalMinutes < 5;

            devices.Add(new
            {
                thingName,
                online = piOnline,
                version = shadow?.Version,
                hostname = shadow?.Hostname,
                lastHeartbeat = shadow?.LastHeartbeat,
                updateStatus = shadow?.UpdateStatus ?? "idle",
                services = shadow?.Services,
                camera = shadow?.Camera,
                lastMotionAt = shadow?.LastMotionAt,
                lastUploadAt = shadow?.LastUploadAt,
                uploadStats = shadow?.UploadStats,
                clipsPending = shadow?.ClipsPending,
                system = shadow?.System,
                config = shadow?.Config,
                configErrors = shadow?.ConfigErrors,
                logShipping = shadow?.LogShipping,
                logShippingError = shadow?.LogShippingError,
                latestVersion,
                updateAvailable = latestVersion != null && shadow?.Version != null && latestVersion != shadow.Version
            });
        }

        return Ok(new { devices, latestVersion });
    }

    [HttpGet("{thingName}/status")]
    public async Task<ActionResult> GetStatus(string thingName)
    {
        var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
        var latestVersion = await _piUpdateService.GetLatestVersionAsync();
        var piOnline = shadow?.LastHeartbeat != null &&
            DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
            (DateTime.UtcNow - lastHb).TotalMinutes < 5;

        return Ok(new
        {
            thingName,
            online = piOnline,
            version = shadow?.Version,
            hostname = shadow?.Hostname,
            lastHeartbeat = shadow?.LastHeartbeat,
            updateStatus = shadow?.UpdateStatus ?? "idle",
            services = shadow?.Services,
            camera = shadow?.Camera,
            lastMotionAt = shadow?.LastMotionAt,
            lastUploadAt = shadow?.LastUploadAt,
            uploadStats = shadow?.UploadStats,
            clipsPending = shadow?.ClipsPending,
            system = shadow?.System,
            config = shadow?.Config,
            configErrors = shadow?.ConfigErrors,
            latestVersion,
            updateAvailable = latestVersion != null && shadow?.Version != null && latestVersion != shadow.Version
        });
    }

    [HttpGet("{thingName}/config")]
    public async Task<ActionResult> GetConfig(string thingName)
    {
        var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
        if (shadow == null) return NotFound(new { error = "Device shadow not found" });
        return Ok(new { config = shadow.Config, configErrors = shadow.ConfigErrors });
    }

    [HttpPost("{thingName}/config")]
    public async Task<ActionResult> UpdateConfig(
        string thingName, [FromBody] Dictionary<string, System.Text.Json.JsonElement> changes)
    {
        if (changes == null || changes.Count == 0)
            return BadRequest(new { error = "No changes provided" });

        var errors = await _piUpdateService.UpdateConfigAsync(thingName, changes);

        if (errors.Count == changes.Count)
            return BadRequest(new { errors });

        return Ok(new { message = "Config update queued", errors });
    }

    [HttpPost("{thingName}/update")]
    public async Task<ActionResult> TriggerUpdate(string thingName, [FromBody] UpdateRequest? request = null)
    {
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
            await _piUpdateService.TriggerUpdateAllAsync(request?.Version);
            var devices = await _piUpdateService.ListPisAsync();
            return Ok(new { message = "Update triggered for all devices", deviceCount = devices.Count, version = request?.Version ?? await _piUpdateService.GetLatestVersionAsync() });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
    [HttpGet("{thingName}/logs")]
    public async Task<ActionResult> GetLogs(
        string thingName,
        [FromQuery] int minutes = 60,
        [FromQuery] string? level = null,
        [FromQuery] string? service = null,
        [FromQuery] int limit = 200)
    {
        var logs = await _logService.GetLogsAsync(thingName, minutes, level, service, limit);
        return Ok(new { logs, thingName, queryMinutes = minutes });
    }
}

public record UpdateRequest(string? Version);
