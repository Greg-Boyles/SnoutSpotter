using System.Text.Json;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClipsController : ControllerBase
{
    private readonly IClipService _clipService;
    private readonly IS3PresignService _presignService;
    private readonly AppConfig _config;

    public ClipsController(IClipService clipService, IS3PresignService presignService, IOptions<AppConfig> config)
    {
        _clipService = clipService;
        _presignService = presignService;
        _config = config.Value;
    }

    [HttpGet]
    public async Task<ActionResult<ClipListResponse>> GetClips(
        [FromQuery] string? date = null,
        [FromQuery] string? device = null,
        [FromQuery] string? detectionType = null,
        [FromQuery] int limit = 20,
        [FromQuery] string? nextPageKey = null)
    {
        var result = await _clipService.GetClipsAsync(HttpContext.GetHouseholdId(), date, device, detectionType, limit, nextPageKey);
        return Ok(result);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ClipDetail>> GetClip(string id)
    {
        var clip = await _clipService.GetClipByIdAsync(id);
        if (clip == null) return NotFound();
        return Ok(clip);
    }

    [HttpGet("{id}/video")]
    public async Task<ActionResult<PresignedUrlResponse>> GetVideoUrl(string id)
    {
        var clip = await _clipService.GetClipByIdAsync(id);
        if (clip == null) return NotFound();

        var url = _presignService.GeneratePresignedUrl(clip.S3Key);
        return Ok(new PresignedUrlResponse(url, 3600));
    }

    [HttpGet("{id}/keyframes")]
    public async Task<ActionResult<List<PresignedUrlResponse>>> GetKeyframeUrls(string id)
    {
        var clip = await _clipService.GetClipByIdAsync(id);
        if (clip == null) return NotFound();

        var urls = clip.KeyframeKeys
            .Select(key => new PresignedUrlResponse(_presignService.GeneratePresignedUrl(key), 3600))
            .ToList();

        return Ok(urls);
    }

    [HttpPost("{id}/infer")]
    public async Task<IActionResult> RunInference(string id)
    {
        var clip = await _clipService.GetClipByIdAsync(id);
        if (clip == null) return NotFound();

        var client = new AmazonLambdaClient();
        await client.InvokeAsync(new InvokeRequest
        {
            FunctionName = _config.InferenceFunction,
            InvocationType = InvocationType.Event,
            Payload = JsonSerializer.Serialize(new { ClipId = id })
        });

        return Accepted(new { message = "Inference started", clipId = id });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteClip(string id)
    {
        await _clipService.DeleteClipAsync(id);
        return NoContent();
    }
}
