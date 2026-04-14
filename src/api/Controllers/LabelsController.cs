using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
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
    private readonly AppConfig _config;

    public LabelsController(
        ILabelService labelService,
        IClipService clipService,
        IOptions<AppConfig> config)
    {
        _labelService = labelService;
        _clipService = clipService;
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