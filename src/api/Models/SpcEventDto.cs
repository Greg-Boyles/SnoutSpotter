namespace SnoutSpotter.Api.Models;

// Server-side shape for a single row of the SPC events timeline as served by
// GET /api/pets/{petId}/spc-events. Weight is a nested object matching the
// structure from SPC's weights[] array. Multi-bowl devices produce multiple
// frames — one per bowl. Negative change = consumed, positive = added/refilled.
public record SpcEventDto(
    string SpcEventId,
    int SpcEventType,
    string EventCategory,
    string CreatedAt,
    string? PetId,
    string? SpcPetId,
    string? DeviceId,
    string? RawData,
    SpcEventWeightDto? Weight);

public record SpcEventWeightDto(
    int Duration,
    int Context,
    List<SpcEventWeightFrameDto> Frames);

public record SpcEventWeightFrameDto(
    int Index,
    int Change,
    int CurrentWeight);

public record SpcEventsPage(
    List<SpcEventDto> Events,
    string? NextPageKey);
