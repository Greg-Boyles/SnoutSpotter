namespace SnoutSpotter.Api.Models;

// Internal helper records representing the nested `spc_integration` map that
// pet and device rows store on disk. The wire DTOs (PetProfile / SpcDeviceDto)
// keep their flat SPC fields — these records only exist to keep the mapping
// code between nested DynamoDB storage and flat DTOs in one place.

public record SpcPetLink(
    string SpcPetId,
    string? SpcPetName,
    string LinkedAt);

public record SpcDeviceLink(
    int? SpcProductId,
    string? SpcName,
    string? SerialNumber,
    string? LastRefreshedAt,
    string LinkedAt);
