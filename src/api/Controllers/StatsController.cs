using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StatsController : ControllerBase
{
    private readonly ClipService _clipService;
    private readonly HealthService _healthService;
    private readonly PiUpdateService _piUpdateService;

    public StatsController(ClipService clipService, HealthService healthService, PiUpdateService piUpdateService)
    {
        _clipService = clipService;
        _healthService = healthService;
        _piUpdateService = piUpdateService;
    }

    [HttpGet]
    public async Task<ActionResult<DashboardStats>> GetStats()
    {
        var today = DateTime.UtcNow.ToString("yyyy/MM/dd");

        var allClips = await _clipService.GetClipsAsync(limit: 1000);
        var todayClips = await _clipService.GetClipsAsync(date: today, limit: 1000);
        var detections = await _clipService.GetDetectionsAsync(limit: 1000);

        var piOnline = await _healthService.IsPiOnlineAsync();

        var stats = new DashboardStats(
            TotalClips: allClips.TotalCount,
            ClipsToday: todayClips.TotalCount,
            TotalDetections: detections.Count,
            MyDogDetections: detections.Count(d => d.DetectionType == "my_dog"),
            LastUploadTime: allClips.Clips.MaxBy(c => c.Timestamp)?.CreatedAt,
            PiOnline: piOnline);

        return Ok(stats);
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
