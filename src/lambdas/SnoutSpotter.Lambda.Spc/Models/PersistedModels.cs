namespace SnoutSpotter.Lambda.Spc.Models;

// Stored on the household record as the spc_integration attribute map.
public record SpcIntegrationState(
    string Status,              // "linked" | "token_expired" | "error"
    string SpcHouseholdId,
    string SpcHouseholdName,
    string SpcUserEmail,
    string SecretArn,
    string LinkedAt,
    string? LastSyncAt,
    string? LastError);

public record LinkRequest(
    string SessionId,
    string SpcHouseholdId,
    List<PetMapping> Mappings);

public record PetMapping(
    string PetId,
    string? SpcPetId,
    string? SpcPetName);

public record LinkResponse(string Status, int MappedCount);

public record StatusResponse(
    string Status,
    string? SpcUserEmail,
    string? SpcHouseholdId,
    string? SpcHouseholdName,
    string? LinkedAt,
    string? LastSyncAt,
    string? LastError);

public record SpcDeviceDto(
    string Id,
    int ProductId,
    string Name,
    string? SerialNumber,
    string? LastActivityAt);

public record SpcDevicesResponse(List<SpcDeviceDto> Devices);

public record UpdatePetLinksRequest(List<PetMapping> Mappings);

public record UpdatePetLinksResponse(int UpdatedCount);
