using System.Text.Json.Serialization;

namespace SnoutSpotter.Spc.Client.Models;

// SPC auth — POST /api/auth/login
// Body is AuthLoginResource per the V1 Swagger. The 200 response body is not
// documented in the spec; shape below tracks the community Home Assistant
// surepy client: { data: { token, user: { id, email_address } } }.

public record SpcLoginRequest(
    [property: JsonPropertyName("client_uid")] string ClientUid,
    [property: JsonPropertyName("email_address")] string EmailAddress,
    [property: JsonPropertyName("password")] string Password);

public record SpcLoginResponse(
    [property: JsonPropertyName("data")] SpcLoginData? Data);

public record SpcLoginData(
    [property: JsonPropertyName("token")] string? Token,
    [property: JsonPropertyName("user")] SpcLoginUser? User);

public record SpcLoginUser(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("email_address")] string? EmailAddress);

// SPC household — GET /api/household
public record SpcHouseholdResource(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name);

// SPC pet — GET /api/household/{householdId}/pet
public record SpcPetResource(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("species_id")] int? SpeciesId,
    [property: JsonPropertyName("photo")] SpcPhotoResource? Photo);

public record SpcPhotoResource(
    [property: JsonPropertyName("location")] string? Location);

// SPC device — GET /api/household/{householdId}/device
public record SpcDeviceResource(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("product_id")] int ProductId,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("serial_number")] string? SerialNumber,
    [property: JsonPropertyName("last_activity_at")] string? LastActivityAt);

// Paginated envelope — SPC wraps list responses in { data: [...], meta: {...} }
public record SpcPaginated<T>(
    [property: JsonPropertyName("data")] List<T>? Data);

// SPC timeline — GET /api/timeline/household/{householdId}?SinceId=...&PageSize=...
// The primary measurement data is in `weights[]`, NOT in `data`. `data` is a
// JSON string that's null/empty for most event types; `weights[].frames[].change`
// carries the actual bowl weight delta per interaction. `data` is still stored
// verbatim for supplementary info on feeding events (tare_value etc.).
public record SpcTimelineResource(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("data")] string? Data,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("pets")] List<SpcTimelinePetRef>? Pets,
    [property: JsonPropertyName("devices")] List<SpcTimelineDeviceRef>? Devices,
    [property: JsonPropertyName("weights")] List<SpcTimelineWeight>? Weights);

public record SpcTimelinePetRef(
    [property: JsonPropertyName("id")] long Id);

public record SpcTimelineDeviceRef(
    [property: JsonPropertyName("id")] long Id);

public record SpcTimelineWeight(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("device_id")] long DeviceId,
    [property: JsonPropertyName("tag_id")] long TagId,
    [property: JsonPropertyName("context")] int Context,
    [property: JsonPropertyName("duration")] int Duration,
    [property: JsonPropertyName("created_at")] string? CreatedAt,
    [property: JsonPropertyName("frames")] List<SpcTimelineWeightFrame>? Frames);

public record SpcTimelineWeightFrame(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("current_weight")] int CurrentWeight,
    [property: JsonPropertyName("change")] int Change,
    [property: JsonPropertyName("created_at")] string? CreatedAt);

// Persisted to AWS Secrets Manager as JSON under snoutspotter/spc/{household_id}.
public record SpcSecret(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("client_uid")] string ClientUid,
    [property: JsonPropertyName("spc_user_id")] long SpcUserId,
    [property: JsonPropertyName("spc_user_email")] string SpcUserEmail);
