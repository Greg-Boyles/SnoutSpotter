using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/ml")]
[Authorize]
public class ModelsController : ControllerBase
{
    private readonly IModelService _modelService;

    public ModelsController(IModelService modelService)
    {
        _modelService = modelService;
    }

    [HttpGet("models")]
    public async Task<ActionResult> ListModels([FromQuery] string type = "classifier")
    {
        var (activeVersion, versions) = await _modelService.ListModelsAsync(HttpContext.GetHouseholdId(), type);

        return Ok(new
        {
            activeVersion,
            versions = versions.Select(v => new
            {
                version = v.Version,
                s3Key = v.S3Key,
                sizeBytes = v.SizeBytes,
                lastModified = v.CreatedAt,
                active = v.Status == "active",
                source = v.Source,
                trainingJobId = v.TrainingJobId,
                notes = v.Notes,
                metrics = v.Metrics
            })
        });
    }

    [HttpPost("models/upload-url")]
    public async Task<ActionResult> GetModelUploadUrl([FromQuery] string version, [FromQuery] string type = "classifier")
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "version is required" });

        var (uploadUrl, s3Key) = await _modelService.GetUploadUrlAsync(HttpContext.GetHouseholdId(), type, version);

        return Ok(new { uploadUrl, s3Key, version, expiresIn = 3600 });
    }

    [HttpPost("models/activate")]
    public async Task<ActionResult> ActivateModel([FromQuery] string version, [FromQuery] string type = "classifier")
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "version is required" });

        try
        {
            await _modelService.ActivateModelAsync(HttpContext.GetHouseholdId(), type, version);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { error = ex.Message });
        }

        return Ok(new { message = $"Activated {type} version '{version}'", version });
    }

    [HttpDelete("models/{type}/{version}")]
    public async Task<ActionResult> DeleteModel(string type, string version)
    {
        try
        {
            await _modelService.DeleteModelAsync(HttpContext.GetHouseholdId(), type, version);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        return Ok(new { message = $"Deleted {type} version '{version}'" });
    }
}
