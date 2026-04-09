using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SettingsController : ControllerBase
{
    private readonly ISettingsService _settingsService;

    public SettingsController(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    [HttpGet]
    public async Task<ActionResult> GetAll()
    {
        var settings = await _settingsService.GetAllAsync();
        return Ok(new { settings = settings.Values });
    }

    [HttpPut("{key}")]
    public async Task<ActionResult> Update(string key, [FromBody] UpdateSettingRequest request)
    {
        try
        {
            await _settingsService.UpdateAsync(key, request.Value);
            return Ok(new { message = $"Setting '{key}' updated", key, value = request.Value });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("reset")]
    public async Task<ActionResult> Reset()
    {
        await _settingsService.ResetAllAsync();
        return Ok(new { message = "All settings reset to defaults" });
    }
}

public record UpdateSettingRequest(string Value);
