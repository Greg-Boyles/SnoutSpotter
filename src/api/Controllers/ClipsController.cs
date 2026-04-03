using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    public ClipsController(IClipService clipService, IS3PresignService presignService)
    {
        _clipService = clipService;
        _presignService = presignService;
    }

    [HttpGet]
    public async Task<ActionResult<ClipListResponse>> GetClips(
        [FromQuery] string? date = null,
        [FromQuery] string? device = null,
        [FromQuery] int limit = 20,
        [FromQuery] string? nextPageKey = null)
    {
        var result = await _clipService.GetClipsAsync(date, device, limit, nextPageKey);
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
}
