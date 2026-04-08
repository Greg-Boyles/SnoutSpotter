using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Lambda.PiMgmt.Services;

namespace SnoutSpotter.Lambda.PiMgmt.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrainersController : ControllerBase
{
    private readonly IDeviceProvisioningService _provisioningService;

    public TrainersController(IDeviceProvisioningService provisioningService)
    {
        _provisioningService = provisioningService;
    }

    [HttpGet]
    public async Task<ActionResult> ListTrainers()
    {
        try
        {
            var trainers = await _provisioningService.ListTrainersAsync();
            return Ok(new { trainers });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("register")]
    public async Task<ActionResult> RegisterTrainer([FromBody] RegisterTrainerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { error = "Trainer name is required" });
        }

        var thingName = $"snoutspotter-trainer-{request.Name}";
        if (!System.Text.RegularExpressions.Regex.IsMatch(thingName, "^[a-zA-Z0-9_-]+$"))
        {
            return BadRequest(new { error = "Trainer name must contain only letters, numbers, hyphens, and underscores" });
        }

        try
        {
            var result = await _provisioningService.RegisterTrainerAsync(thingName);
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
    public async Task<ActionResult> DeregisterTrainer(string thingName)
    {
        try
        {
            await _provisioningService.DeregisterTrainerAsync(thingName);
            return Ok(new { message = $"Trainer '{thingName}' deregistered successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public record RegisterTrainerRequest(string Name);
