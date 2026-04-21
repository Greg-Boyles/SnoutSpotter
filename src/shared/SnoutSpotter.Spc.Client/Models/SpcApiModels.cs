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

// Persisted to AWS Secrets Manager as JSON under snoutspotter/spc/{household_id}.
public record SpcSecret(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("issued_at")] string IssuedAt,
    [property: JsonPropertyName("client_uid")] string ClientUid,
    [property: JsonPropertyName("spc_user_id")] long SpcUserId,
    [property: JsonPropertyName("spc_user_email")] string SpcUserEmail);
