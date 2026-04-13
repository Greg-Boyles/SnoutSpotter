using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;
using SnoutSpotter.Contracts;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/ml")]
[Authorize]
public class LabelsController : ControllerBase
{
    private readonly ILabelService _labelService;
    private readonly IClipService _clipService;
    private readonly IExportService _exportService;
    private readonly IModelService _modelService;
    private readonly AppConfig _config;

    public LabelsController(
        ILabelService labelService,
        IClipService clipService,
        IExportService exportService,
        IModelService modelService,
        IOptions<AppConfig> config)
    {
        _exportService = exportService;
        _labelService = labelService;
        _clipService = clipService;
        _modelService = modelService;
        _config = config.Value;
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
        [FromQuery] string? device = null,
        [FromQuery] int limit = 50,
        [FromQuery] string? nextPageKey = null)
    {
        var (items, nextKey) = await _labelService.GetLabelsAsync(reviewed, label, confirmedLabel, breed, device, limit, nextPageKey);

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

    [HttpPatch("labels/bounding-boxes")]
    public async Task<ActionResult> UpdateBoundingBoxes([FromBody] UpdateBoundingBoxesRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.KeyframeKey))
            return BadRequest(new { error = "keyframeKey is required" });
        if (request.Boxes == null)
            return BadRequest(new { error = "boxes is required" });

        // Append confidence 1.0 to each manually-drawn box [x, y, w, h] → [x, y, w, h, 1.0]
        var boxes = request.Boxes.Select(b =>
        {
            if (b.Length < 4) throw new ArgumentException("Each box must have 4 elements [x, y, w, h]");
            return new float[] { b[0], b[1], b[2], b[3], 1.0f };
        }).ToList();

        await _labelService.UpdateBoundingBoxesAsync(request.KeyframeKey, boxes);
        return Ok(new { message = "Bounding boxes updated", count = boxes.Count });
    }

    [HttpGet("labels/{*keyframeKey}")]
    public async Task<ActionResult> GetLabel(string keyframeKey)
    {
        var item = await _labelService.GetLabelAsync(keyframeKey);
        if (item == null)
            return NotFound(new { error = "Label not found" });
        return Ok(item);
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

    [HttpPost("labels/backfill-breed")]
    public async Task<ActionResult> BackfillBreed([FromBody] BackfillBreedRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ConfirmedLabel))
            return BadRequest(new { error = "confirmedLabel is required" });
        if (string.IsNullOrWhiteSpace(request.Breed))
            return BadRequest(new { error = "breed is required" });

        var count = await _labelService.BackfillBreedAsync(request.ConfirmedLabel, request.Breed);
        return Ok(new { message = $"Updated {count} labels with breed '{request.Breed}'" , updated = count });
    }

    [HttpPost("labels/backfill-boxes")]
    public async Task<ActionResult> BackfillBoundingBoxes([FromBody] BackfillBoxesRequest? request = null)
    {
        var confirmedLabel = request?.ConfirmedLabel;
        var keys = request?.Keys;

        if (confirmedLabel != null && confirmedLabel is not ("my_dog" or "other_dog"))
            return BadRequest(new { error = "confirmedLabel must be 'my_dog' or 'other_dog'" });

        var result = await _labelService.BackfillBoundingBoxesAsync(confirmedLabel, keys);
        return Ok(result);
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
    public async Task<ActionResult> TriggerExport([FromBody] TriggerExportRequest? request = null)
    {
        try
        {
            var exportId = await _exportService.TriggerExportAsync(
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

    public record TriggerExportRequest(
        int? MaxPerClass = null,
        bool IncludeBackground = true,
        float BackgroundRatio = 1.0f,
        string ExportType = "detection",
        float CropPadding = 0.1f,
        bool MergeClasses = false);

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

    [HttpGet("models")]
    public async Task<ActionResult> ListModels([FromQuery] string type = "classifier")
    {
        var (activeVersion, versions) = await _modelService.ListModelsAsync(type);

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

        var (uploadUrl, s3Key) = await _modelService.GetUploadUrlAsync(type, version);

        return Ok(new { uploadUrl, s3Key, version, expiresIn = 3600 });
    }

    [HttpPost("models/activate")]
    public async Task<ActionResult> ActivateModel([FromQuery] string version, [FromQuery] string type = "classifier")
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "version is required" });

        try
        {
            await _modelService.ActivateModelAsync(type, version);
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
            await _modelService.DeleteModelAsync(type, version);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        return Ok(new { message = $"Deleted {type} version '{version}'" });
    }

    [HttpPost("rerun-inference")]
    public async Task<ActionResult> RerunInference([FromBody] RerunInferenceRequest? request = null)
    {
        // Accept explicit clip IDs or query by date range
        var clipIds = request?.ClipIds?.Count > 0
            ? request.ClipIds
            : await _clipService.GetClipIdsForDateRangeAsync(request?.DateFrom, request?.DateTo);
        if (clipIds.Count == 0)
            return Ok(new { total = 0, queued = 0 });

        if (string.IsNullOrEmpty(_config.RerunInferenceQueueUrl))
            return StatusCode(503, new { error = "Rerun inference queue not configured" });

        using var sqsClient = new Amazon.SQS.AmazonSQSClient();
        var queued = 0;

        // Send in batches of 10 (SQS batch limit)
        foreach (var batch in clipIds.Chunk(10))
        {
            var entries = batch.Select((clipId, i) => new Amazon.SQS.Model.SendMessageBatchRequestEntry
            {
                Id = i.ToString(),
                MessageBody = System.Text.Json.JsonSerializer.Serialize(new InferenceMessage(clipId))
            }).ToList();

            await sqsClient.SendMessageBatchAsync(new Amazon.SQS.Model.SendMessageBatchRequest
            {
                QueueUrl = _config.RerunInferenceQueueUrl,
                Entries = entries
            });
            queued += entries.Count;
        }

        return Ok(new { total = clipIds.Count, queued });
    }
}

public record RerunInferenceRequest(string? DateFrom = null, string? DateTo = null, List<string>? ClipIds = null);

public record UpdateLabelRequest(string ConfirmedLabel, string? Breed = null);
public record BulkConfirmRequest(List<string> KeyframeKeys, string ConfirmedLabel, string? Breed = null);
public record BackfillBreedRequest(string ConfirmedLabel, string Breed);
public record BackfillBoxesRequest(string? ConfirmedLabel = null, List<string>? Keys = null);
public record UpdateBoundingBoxesRequest(string KeyframeKey, List<float[]> Boxes);
