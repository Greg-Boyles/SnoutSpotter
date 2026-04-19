using System.Text.Json.Serialization;

namespace SnoutSpotter.Lambda.Spc.Models;

// Persisted to AWS Secrets Manager as JSON under snoutspotter/spc/{household_id}.
public record SpcSecret(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("client_uid")] string ClientUid,
    [property: JsonPropertyName("spc_user_id")] long SpcUserId,
    [property: JsonPropertyName("spc_user_email")] string SpcUserEmail);

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
