using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class StatsController : ControllerBase
{
    private readonly IClipService _clipService;
    private readonly IPiUpdateService _piUpdateService;

    public StatsController(IClipService clipService, IPiUpdateService piUpdateService)
    {
        _clipService = clipService;
        _piUpdateService = piUpdateService;
    }

    [HttpGet]
    public async Task<ActionResult<DashboardStats>> GetStats()
    {
        var today = DateTime.UtcNow.ToString("yyyy/MM/dd");

        var allClips = await _clipService.GetClipsAsync(limit: 1000);
        var todayClips = await _clipService.GetClipsAsync(date: today, limit: 1000);
        var detections = await _clipService.GetDetectionsAsync(limit: 1000);

        var thingNames = await _piUpdateService.ListPisAsync();
        var piOnlineCount = 0;
        string? lastUploadAcrossAll = null;

        foreach (var thingName in thingNames)
        {
            var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
            if (shadow?.LastHeartbeat != null &&
                DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
                (DateTime.UtcNow - lastHb).TotalMinutes < 5)
            {
                piOnlineCount++;
            }

            if (shadow?.LastUploadAt != null &&
                (lastUploadAcrossAll == null || string.Compare(shadow.LastUploadAt, lastUploadAcrossAll, StringComparison.Ordinal) > 0))
            {
                lastUploadAcrossAll = shadow.LastUploadAt;
            }
        }

        var lastUploadTime = lastUploadAcrossAll ?? allClips.Clips.MaxBy(c => c.Timestamp)?.CreatedAt;

        var stats = new DashboardStats(
            TotalClips: allClips.TotalCount,
            ClipsToday: todayClips.TotalCount,
            TotalDetections: detections.Count,
            MyDogDetections: detections.Count(d => d.DetectionType == "my_dog"),
            LastUploadTime: lastUploadTime,
            PiOnlineCount: piOnlineCount,
            PiTotalCount: thingNames.Count);

        return Ok(stats);
    }

    [HttpGet("activity")]
    public async Task<ActionResult<object>> GetActivity([FromQuery] int days = 14)
    {
        var tasks = Enumerable.Range(0, days)
            .Select(i => DateTime.UtcNow.AddDays(-(days - 1 - i)))
            .Select(async d => new
            {
                date = d.ToString("yyyy-MM-dd"),
                count = await _clipService.GetClipCountForDateAsync(d.ToString("yyyy/MM/dd"))
            });

        var activity = await Task.WhenAll(tasks);
        return Ok(new { activity });
    }

    [HttpGet("health")]
    public async Task<ActionResult<object>> GetHealth()
    {
        var thingNames = await _piUpdateService.ListPisAsync();
        var latestVersion = await _piUpdateService.GetLatestVersionAsync();

        var devices = new List<object>();
        foreach (var thingName in thingNames)
        {
            var shadow = await _piUpdateService.GetPiShadowAsync(thingName);
            var piOnline = shadow?.LastHeartbeat != null &&
                DateTime.TryParse(shadow.LastHeartbeat, out var lastHb) &&
                (DateTime.UtcNow - lastHb).TotalMinutes < 5;

            devices.Add(new
            {
                thingName,
                online = piOnline,
                version = shadow?.Version,
                hostname = shadow?.Hostname,
                lastHeartbeat = shadow?.LastHeartbeat,
                updateStatus = shadow?.UpdateStatus ?? "idle",
                services = shadow?.Services,
                camera = shadow?.Camera,
                lastMotionAt = shadow?.LastMotionAt,
                lastUploadAt = shadow?.LastUploadAt,
                uploadStats = shadow?.UploadStats,
                clipsPending = shadow?.ClipsPending,
                system = shadow?.System,
                updateAvailable = latestVersion != null && shadow?.Version != null && latestVersion != shadow.Version
            });
        }

        return Ok(new
        {
            checkedAt = DateTime.UtcNow.ToString("O"),
            latestVersion,
            devices
        });
    }
}
