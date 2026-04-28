namespace SnoutSpotter.Api.Models;

// Server-side shape for a single row of the SPC events timeline as served by
// GET /api/pets/{petId}/spc-events. Weight fields are the primary measurement
// data from SPC's weights[] array on each timeline event. Negative change =
// consumed, positive = added/refilled. RawData is the verbatim SPC `data` JSON
// string kept for supplementary info (tare_value, food_type on feeding events).
public record SpcEventDto(
    string SpcEventId,
    int SpcEventType,
    string EventCategory,
    string CreatedAt,
    string? PetId,
    string? SpcPetId,
    string? DeviceId,
    string? RawData,
    int? WeightChange,
    int? WeightDuration,
    int? WeightCurrent);

public record SpcEventsPage(
    List<SpcEventDto> Events,
    string? NextPageKey);
