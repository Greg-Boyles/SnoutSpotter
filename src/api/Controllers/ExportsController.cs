using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/ml")]
[Authorize]
public class ExportsController : ControllerBase
{
    private readonly IExportService _exportService;

    public ExportsController(IExportService exportService)
    {
        _exportService = exportService;
    }

    [HttpPost("export")]
    public async Task<ActionResult> TriggerExport([FromBody] TriggerExportRequest? request = null)
    {
        try
        {
            var exportId = await _exportService.TriggerExportAsync(
                HttpContext.GetHouseholdId(),
                request?.MaxPerClass,
                request?.IncludeBackground ?? true,
                request?.BackgroundRatio ?? 1.0f,
                request?.ExportType ?? "detection",
                request?.CropPadding ?? 0.1f,
                request?.MergeClasses ?? false);
            return Ok(new { exportId, message = "Export started" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("exports")]
    public async Task<ActionResult> ListExports()
    {
        var exports = await _exportService.ListExportsAsync(HttpContext.GetHouseholdId());
        return Ok(new { exports });
    }

    [HttpGet("exports/{exportId}/download")]
    public async Task<ActionResult> GetExportDownload(string exportId)
    {
        var url = await _exportService.GetDownloadUrlAsync(exportId);
        if (url == null)
            return NotFound(new { error = "Export not found or not ready" });
        return Ok(new { downloadUrl = url });
    }

    [HttpDelete("exports/{exportId}")]
    public async Task<ActionResult> DeleteExport(string exportId)
    {
        await _exportService.DeleteExportAsync(exportId);
        return Ok(new { message = "Export deleted" });
    }
}