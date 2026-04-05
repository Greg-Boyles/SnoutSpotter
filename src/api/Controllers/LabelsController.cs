using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/ml")]
[Authorize]
public class LabelsController : ControllerBase
{
    private readonly ILabelService _labelService;
    private readonly IExportService _exportService;
    private readonly IS3PresignService _presignService;
    private readonly IAmazonS3 _s3;
    private readonly string _bucketName;

    private const string ClassifierPrefix = "models/dog-classifier/versions/";
    private const string ClassifierActiveKey = "models/dog-classifier/best.onnx";
    private const string ClassifierActiveVersionKey = "models/dog-classifier/active.json";

    public LabelsController(
        ILabelService labelService,
        IExportService exportService,
        IS3PresignService presignService,
        IAmazonS3 s3,
        IOptions<AppConfig> config)
    {
        _exportService = exportService;
        _labelService = labelService;
        _presignService = presignService;
        _s3 = s3;
        _bucketName = config.Value.BucketName;
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

    [HttpGet("models")]
    public async Task<ActionResult> ListModels()
    {
        // Get active version
        string? activeVersion = null;
        try
        {
            var activeObj = await _s3.GetObjectAsync(_bucketName, ClassifierActiveVersionKey);
            using var reader = new StreamReader(activeObj.ResponseStream);
            var json = await reader.ReadToEndAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            activeVersion = doc.RootElement.GetProperty("version").GetString();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound) { }

        // List all versions
        var versions = new List<object>();
        var listResponse = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucketName,
            Prefix = ClassifierPrefix
        });

        foreach (var obj in listResponse.S3Objects)
        {
            var fileName = obj.Key[ClassifierPrefix.Length..];
            if (!fileName.EndsWith(".onnx")) continue;
            var version = fileName[..^5]; // strip .onnx

            versions.Add(new
            {
                version,
                s3Key = obj.Key,
                sizeBytes = obj.Size,
                lastModified = obj.LastModified.ToString("O"),
                active = version == activeVersion
            });
        }

        return Ok(new { activeVersion, versions });
    }

    [HttpPost("models/upload-url")]
    public ActionResult GetModelUploadUrl([FromQuery] string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "version is required" });

        var s3Key = $"{ClassifierPrefix}{version}.onnx";
        var uploadUrl = _presignService.GeneratePresignedPutUrl(s3Key, "application/octet-stream");

        return Ok(new { uploadUrl, s3Key, version, expiresIn = 3600 });
    }

    [HttpPost("models/activate")]
    public async Task<ActionResult> ActivateModel([FromQuery] string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return BadRequest(new { error = "version is required" });

        var sourceKey = $"{ClassifierPrefix}{version}.onnx";

        // Verify the version exists
        try
        {
            await _s3.GetObjectMetadataAsync(_bucketName, sourceKey);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return NotFound(new { error = $"Version '{version}' not found" });
        }

        // Copy version to best.onnx
        await _s3.CopyObjectAsync(_bucketName, sourceKey, _bucketName, ClassifierActiveKey);

        // Write active.json
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = ClassifierActiveVersionKey,
            ContentBody = System.Text.Json.JsonSerializer.Serialize(new { version }),
            ContentType = "application/json"
        });

        return Ok(new { message = $"Activated version '{version}'", version });
    }
}

public record UpdateLabelRequest(string ConfirmedLabel, string? Breed = null);
public record BulkConfirmRequest(List<string> KeyframeKeys, string ConfirmedLabel, string? Breed = null);
public record BackfillBreedRequest(string ConfirmedLabel, string Breed);
public record BackfillBoxesRequest(string? ConfirmedLabel = null, List<string>? Keys = null);
