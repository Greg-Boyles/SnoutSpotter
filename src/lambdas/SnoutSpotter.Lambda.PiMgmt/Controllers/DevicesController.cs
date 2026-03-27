using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Lambda.PiMgmt.Services;

namespace SnoutSpotter.Lambda.PiMgmt.Controllers;

[ApiController]
[Route("api/[controller]")]
public class DevicesController : ControllerBase
{
    private readonly DeviceProvisioningService _provisioningService;

    public DevicesController(DeviceProvisioningService provisioningService)
    {
        _provisioningService = provisioningService;
    }

    [HttpGet]
    public async Task<ActionResult<List<string>>> ListDevices()
    {
        try
        {
            var devices = await _provisioningService.ListDevicesAsync();
            return Ok(new { devices });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("register")]
    public async Task<ActionResult> RegisterDevice([FromBody] RegisterRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Device name is required" });
        }

        // Sanitize device name - only allow alphanumeric, hyphens, and underscores
        var thingName = $"snoutspotter-{request.Name}";
        if (!System.Text.RegularExpressions.Regex.IsMatch(thingName, "^[a-zA-Z0-9_-]+$"))
        {
            return BadRequest(new { error = "Device name must contain only letters, numbers, hyphens, and underscores" });
        }

        try
        {
            var result = await _provisioningService.RegisterAsync(thingName);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpDelete("{thingName}")]
    public async Task<ActionResult> DeregisterDevice(string thingName)
    {
        try
        {
            await _provisioningService.DeregisterAsync(thingName);
            return Ok(new { message = $"Device '{thingName}' deregistered successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public record RegisterRequest(string Name);
