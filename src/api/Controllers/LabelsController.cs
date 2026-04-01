using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Services;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/ml")]
[Authorize]
public class LabelsController : ControllerBase
{
    private readonly ILabelService _labelService;
    private readonly IExportService _exportService;

    public LabelsController(ILabelService labelService, IExportService exportService)
    {
        _exportService = exportService;
        _labelService = labelService;
    }

    [HttpPost("auto-label")]
    public async Task<ActionResult> TriggerAutoLabel([FromQuery] string? date = null)
    {
        try
        {
            var result = await _labelService.TriggerAutoLabelAsync(date);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpGet("labels/stats")]
    public async Task<ActionResult> GetStats()
    {
        var stats = await _labelService.GetStatsAsync();
        return Ok(stats);
    }

    [HttpGet("labels")]
    public async Task<ActionResult> GetLabels(
        [FromQuery] string? reviewed = null,
        [FromQuery] string? label = null,
        [FromQuery] string? confirmedLabel = null,
        [FromQuery] string? breed = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? nextPageKey = null)
    {
        var (items, nextKey) = await _labelService.GetLabelsAsync(reviewed, label, confirmedLabel, breed, limit, nextPageKey);

        // Add presigned URLs for each keyframe
        var enriched = items.Select(item =>
        {
            var dict = new Dictionary<string, string?>(item);
            if (item.TryGetValue("keyframe_key", out var key))
                dict["imageUrl"] = _labelService.GetPresignedUrl(key);
            return dict;
        }).ToList();

        return Ok(new { labels = enriched, nextPageKey = nextKey });
    }

    [HttpPut("labels/{*keyframeKey}")]
    public async Task<ActionResult> UpdateLabel(string keyframeKey, [FromBody] UpdateLabelRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConfirmedLabel))
            return BadRequest(new { error = "confirmedLabel is required" });

        if (request.ConfirmedLabel is not ("my_dog" or "other_dog" or "no_dog"))
            return BadRequest(new { error = "confirmedLabel must be 'my_dog', 'other_dog', or 'no_dog'" });

        await _labelService.UpdateLabelAsync(keyframeKey, request.ConfirmedLabel, request.Breed);
        return Ok(new { message = "Label updated" });
    }

    [HttpPost("labels/bulk-confirm")]
    public async Task<ActionResult> BulkConfirm([FromBody] BulkConfirmRequest request)
    {
        if (request.KeyframeKeys == null || request.KeyframeKeys.Count == 0)
            return BadRequest(new { error = "keyframeKeys is required" });

        if (request.ConfirmedLabel is not ("my_dog" or "other_dog" or "no_dog"))
            return BadRequest(new { error = "confirmedLabel must be 'my_dog', 'other_dog', or 'no_dog'" });

        await _labelService.BulkConfirmAsync(request.KeyframeKeys, request.ConfirmedLabel, request.Breed);
        return Ok(new { message = $"Updated {request.KeyframeKeys.Count} labels" });
    }

    [HttpPost("labels/upload")]
    [RequestSizeLimit(100 * 1024 * 1024)] // 100MB total
    public async Task<ActionResult> UploadTrainingImages([FromQuery] string label = "my_dog", [FromQuery] string? breed = null)
    {
        if (label is not ("my_dog" or "other_dog" or "no_dog"))
            return BadRequest(new { error = "label must be 'my_dog', 'other_dog', or 'no_dog'" });

        var files = Request.Form.Files;
        if (files.Count == 0)
            return BadRequest(new { error = "No files uploaded" });

        var allowedTypes = new HashSet<string> { "image/jpeg", "image/png", "image/jpg" };
        var results = new List<object>();
        var errors = new List<string>();

        foreach (var file in files)
        {
            if (!allowedTypes.Contains(file.ContentType.ToLowerInvariant()))
            {
                errors.Add($"{file.FileName}: unsupported type {file.ContentType}");
                continue;
            }

            if (file.Length > 10 * 1024 * 1024)
            {
                errors.Add($"{file.FileName}: exceeds 10MB limit");
                continue;
            }

            try
            {
                var result = await _labelService.UploadTrainingImageAsync(
                    file.OpenReadStream(), file.FileName, label, breed);
                results.Add(result);
            }
            catch (Exception ex)
            {
                errors.Add($"{file.FileName}: {ex.Message}");
            }
        }

        return Ok(new { uploaded = results.Count, errors, labels = results });
    }

    [HttpPost("export")]
    public async Task<ActionResult> TriggerExport()
    {
        try
        {
            var exportId = await _exportService.TriggerExportAsync();
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
        var exports = await _exportService.ListExportsAsync();
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

public record UpdateLabelRequest(string ConfirmedLabel, string? Breed = null);
public record BulkConfirmRequest(List<string> KeyframeKeys, string ConfirmedLabel, string? Breed = null);
