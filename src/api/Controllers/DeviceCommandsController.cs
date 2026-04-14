using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/device")]
[Authorize]
public class DeviceCommandsController : ControllerBase
{
    private readonly IPiUpdateService _piUpdateService;
    private readonly ILogService _logService;

    public DeviceCommandsController(IPiUpdateService piUpdateService, ILogService logService)
    {
        _piUpdateService = piUpdateService;
        _logService = logService;
    }

    [HttpPost("{thingName}/command")]
    public async Task<ActionResult> SendCommand(string thingName, [FromBody] CommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Action))
            return BadRequest(new { error = "Action is required" });

        try
        {
            var commandId = await _piUpdateService.SendCommandAsync(thingName, request.Action);
            return Ok(new { commandId, message = $"Command '{request.Action}' sent to {thingName}" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("{thingName}/command/{commandId}")]
    public async Task<ActionResult> GetCommandResult(string thingName, string commandId)
    {
        var result = await _piUpdateService.GetCommandFromLedgerAsync(commandId);
        if (result == null)
            return NotFound(new { error = "Command not found" });

        return Ok(new
        {
            commandId,
            status = result.GetValueOrDefault("status", "sent"),
            action = result.GetValueOrDefault("action"),
            message = result.GetValueOrDefault("message"),
            error = result.GetValueOrDefault("error"),
            requestedAt = result.GetValueOrDefault("requested_at"),
            completedAt = result.GetValueOrDefault("completed_at"),
        });
    }

    [HttpGet("{thingName}/commands")]
    public async Task<ActionResult> GetCommandHistory(string thingName, [FromQuery] int limit = 50)
    {
        var commands = await _piUpdateService.GetCommandHistoryAsync(thingName, limit);
        return Ok(new { commands, thingName });
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
