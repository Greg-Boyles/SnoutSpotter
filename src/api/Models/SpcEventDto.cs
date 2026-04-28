namespace SnoutSpotter.Api.Models;

// Server-side shape for a single row of the SPC events timeline as served by
// GET /api/pets/{petId}/spc-events. RawData is the verbatim SPC `data` JSON
// string, exposed as an opaque field so richer decoding can land later without
// another API change.
public record SpcEventDto(
    string SpcEventId,
    int SpcEventType,
    string EventCategory,
    string CreatedAt,
    string? PetId,
    string? SpcPetId,
    string? DeviceId,
    string? RawData);

public record SpcEventsPage(
    List<SpcEventDto> Events,
    string? NextPageKey);
