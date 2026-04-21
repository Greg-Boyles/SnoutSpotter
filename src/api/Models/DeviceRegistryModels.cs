namespace SnoutSpotter.Api.Models;

// Shape of the GET /api/devices response. Each household query returns all
// three row kinds in one call; the server partitions them for the frontend.
public record DeviceListResponse(
    List<SnoutSpotterDeviceDto> SnoutSpotter,
    List<SpcDeviceDto> Spc,
    List<DeviceLinkDto> Links);

public record SnoutSpotterDeviceDto(
    string ThingName,
    string DisplayName,
    string? Notes,
    string CreatedAt,
    string UpdatedAt);

public record SpcDeviceDto(
    string SpcDeviceId,
    int? SpcProductId,
    string? SpcName,
    string? SerialNumber,
    string DisplayName,
    string? Notes,
    string? LastRefreshedAt,
    string CreatedAt,
    string UpdatedAt);

public record DeviceLinkDto(
    string SpcDeviceId,
    string ThingName,
    string CreatedAt);

// Request bodies.
public record UpdateDeviceRequest(string DisplayName, string? Notes);

public record CreateLinkRequest(string SpcDeviceId, string SnoutspotterThingName);
