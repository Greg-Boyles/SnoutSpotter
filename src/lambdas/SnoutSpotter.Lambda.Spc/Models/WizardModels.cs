namespace SnoutSpotter.Lambda.Spc.Models;

public record ValidateRequest(string Email, string Password);

public record ValidateResponse(string SessionId, string SpcUserEmail, string ExpiresAt);

public record SpcHouseholdDto(string Id, string Name);

public record SpcHouseholdsResponse(List<SpcHouseholdDto> Households);

public record SpcPetDto(string Id, string Name, string? Species, string? PhotoUrl);

public record SpcPetsResponse(List<SpcPetDto> Pets);

// Ephemeral wizard session — only lives in IMemoryCache between /validate and /link.
public record SpcSession(
    string SessionId,
    string AccessToken,
    string ClientUid,
    long SpcUserId,
    string SpcUserEmail,
    DateTime ExpiresAt);
