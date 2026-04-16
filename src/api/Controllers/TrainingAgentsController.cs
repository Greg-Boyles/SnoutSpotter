using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/training")]
[Authorize]
public class TrainingAgentsController : ControllerBase
{
    private readonly ITrainingService _trainingService;

    public TrainingAgentsController(ITrainingService trainingService)
    {
        _trainingService = trainingService;
    }

    [HttpGet("agents")]
    public async Task<ActionResult> ListAgents()
    {
        var agents = await _trainingService.ListAgentsAsync();
        return Ok(new { agents });
    }

    [HttpGet("agents/releases")]
    public async Task<ActionResult> ListReleases()
    {
        var releases = await _trainingService.ListAgentReleasesAsync();
        var latest = await _trainingService.GetLatestAgentVersionAsync();
        return Ok(new { releases, latestVersion = latest });
    }

    [HttpGet("agents/{thingName}")]
    public async Task<ActionResult> GetAgentStatus(string thingName)
    {
        var status = await _trainingService.GetAgentStatusAsync(thingName);
        if (status == null) return NotFound(new { error = $"Agent '{thingName}' not found" });
        return Ok(status);
    }

    [HttpPost("agents/{thingName}/update")]
    public async Task<ActionResult> TriggerAgentUpdate(string thingName, [FromBody] AgentUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Version))
            return BadRequest(new { error = "Version is required" });

        await _trainingService.TriggerAgentUpdateAsync(thingName, request.Version);
        return Ok(new { message = $"Update to v{request.Version} triggered for {thingName}" });
    }
}