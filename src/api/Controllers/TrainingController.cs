using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TrainingController : ControllerBase
{
    private readonly ITrainingService _trainingService;

    public TrainingController(ITrainingService trainingService)
    {
        _trainingService = trainingService;
    }

    [HttpGet("agents")]
    public async Task<ActionResult> ListAgents()
    {
        var agents = await _trainingService.ListAgentsAsync();
        return Ok(new { agents });
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

    [HttpPost("jobs")]
    public async Task<ActionResult> SubmitJob([FromBody] TrainingJobRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ExportId) || string.IsNullOrWhiteSpace(request.ExportS3Key))
            return BadRequest(new { error = "ExportId and ExportS3Key are required" });

        var jobId = await _trainingService.SubmitJobAsync(request);
        return Ok(new { jobId });
    }

    [HttpGet("jobs")]
    public async Task<ActionResult> ListJobs([FromQuery] string? status = null, [FromQuery] int limit = 50)
    {
        var jobs = await _trainingService.ListJobsAsync(status, limit);
        return Ok(new { jobs });
    }

    [HttpGet("jobs/{jobId}")]
    public async Task<ActionResult> GetJob(string jobId)
    {
        var job = await _trainingService.GetJobAsync(jobId);
        if (job == null) return NotFound(new { error = $"Job '{jobId}' not found" });
        return Ok(job);
    }

    [HttpPost("jobs/{jobId}/cancel")]
    public async Task<ActionResult> CancelJob(string jobId)
    {
        try
        {
            await _trainingService.CancelJobAsync(jobId);
            return Ok(new { message = $"Cancel requested for {jobId}" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("jobs/{jobId}")]
    public async Task<ActionResult> DeleteJob(string jobId)
    {
        try
        {
            await _trainingService.DeleteJobAsync(jobId);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }
}

public record AgentUpdateRequest(string Version);
