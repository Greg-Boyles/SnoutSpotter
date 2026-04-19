using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Lambda.Spc.Extensions;
using SnoutSpotter.Lambda.Spc.Models;
using SnoutSpotter.Lambda.Spc.Services;

namespace SnoutSpotter.Lambda.Spc.Controllers;

[ApiController]
[Route("api/integrations/spc")]
[Authorize]
public class SpcIntegrationController : ControllerBase
{
    private readonly ISpcApiClient _spc;
    private readonly ISpcSessionStore _sessions;
    private readonly ISpcSecretsStore _secrets;
    private readonly IHouseholdIntegrationService _households;
    private readonly IPetLinkService _petLinks;
    private readonly ILogger<SpcIntegrationController> _log;

    public SpcIntegrationController(
        ISpcApiClient spc,
        ISpcSessionStore sessions,
        ISpcSecretsStore secrets,
        IHouseholdIntegrationService households,
        IPetLinkService petLinks,
        ILogger<SpcIntegrationController> log)
    {
        _spc = spc;
        _sessions = sessions;
        _secrets = secrets;
        _households = households;
        _petLinks = petLinks;
        _log = log;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct)
    {
        var householdId = HttpContext.GetHouseholdId();
        var state = await _households.GetAsync(householdId, ct);
        if (state == null)
            return Ok(new StatusResponse("unlinked", null, null, null, null, null, null));
        return Ok(new StatusResponse(
            state.Status,
            state.SpcUserEmail,
            state.SpcHouseholdId,
            state.SpcHouseholdName,
            state.LinkedAt,
            state.LastSyncAt,
            state.LastError));
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "email_and_password_required" });

        var householdId = HttpContext.GetHouseholdId();
        // Reuse the existing client_uid if we already have one persisted for this household
        // so SPC sees a stable client identifier across re-links; otherwise mint a fresh GUID.
        var existing = await _secrets.GetAsync(householdId, ct);
        var clientUid = existing?.ClientUid ?? Guid.NewGuid().ToString();

        try
        {
            var login = await _spc.LoginAsync(req.Email, req.Password, clientUid, ct);
            var session = _sessions.Create(login.Token!, clientUid, login.User!.Id, login.User.EmailAddress ?? req.Email);
            return Ok(new ValidateResponse(session.SessionId, session.SpcUserEmail, session.ExpiresAt.ToString("O")));
        }
        catch (SpcUnauthorizedException)
        {
            return Unauthorized(new { error = "invalid_credentials" });
        }
        catch (SpcUpstreamException ex)
        {
            _log.LogWarning(ex, "SPC login upstream failure");
            return StatusCode(502, new { error = "upstream_unavailable" });
        }
    }

    [HttpGet("spc-households")]
    public async Task<IActionResult> ListSpcHouseholds([FromQuery] string session, CancellationToken ct)
    {
        var (active, err) = ResolveSession(session);
        if (active == null) return err!;
        try
        {
            var raw = await _spc.ListHouseholdsAsync(active.AccessToken, ct);
            var dto = raw
                .Select(h => new SpcHouseholdDto(h.Id.ToString(), h.Name ?? $"Household {h.Id}"))
                .ToList();
            return Ok(new SpcHouseholdsResponse(dto));
        }
        catch (SpcUnauthorizedException)
        {
            return Unauthorized(new { error = "session_invalid" });
        }
        catch (SpcUpstreamException ex)
        {
            _log.LogWarning(ex, "SPC households fetch upstream failure");
            return StatusCode(502, new { error = "upstream_unavailable" });
        }
    }

    [HttpGet("spc-pets")]
    public async Task<IActionResult> ListSpcPets([FromQuery] string session, [FromQuery] string spcHouseholdId, CancellationToken ct)
    {
        if (!long.TryParse(spcHouseholdId, out var parsedHouseholdId))
            return BadRequest(new { error = "spc_household_id_invalid" });

        var (active, err) = ResolveSession(session);
        if (active == null) return err!;

        try
        {
            var raw = await _spc.ListPetsAsync(active.AccessToken, parsedHouseholdId, ct);
            // Surface all species — user-configurable mapping intentionally unrestricted.
            var dto = raw
                .Select(p => new SpcPetDto(
                    Id: p.Id.ToString(),
                    Name: p.Name ?? $"Pet {p.Id}",
                    Species: p.SpeciesId?.ToString(),
                    PhotoUrl: p.Photo?.Location))
                .ToList();
            return Ok(new SpcPetsResponse(dto));
        }
        catch (SpcUnauthorizedException)
        {
            return Unauthorized(new { error = "session_invalid" });
        }
        catch (SpcUpstreamException ex)
        {
            _log.LogWarning(ex, "SPC pets fetch upstream failure");
            return StatusCode(502, new { error = "upstream_unavailable" });
        }
    }

    [HttpPost("link")]
    public async Task<IActionResult> Link([FromBody] LinkRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.SpcHouseholdId))
            return BadRequest(new { error = "spc_household_id_required" });
        if (req.Mappings == null)
            return BadRequest(new { error = "mappings_required" });

        var (active, err) = ResolveSession(req.SessionId);
        if (active == null) return err!;

        var householdId = HttpContext.GetHouseholdId();

        // Validate: every SnoutSpotter pet must have an explicit entry (even if SpcPetId is null).
        var ourPetIds = (await _petLinks.ListPetIdsAsync(householdId, ct)).ToHashSet();
        var mappedPetIds = req.Mappings.Select(m => m.PetId).ToHashSet();
        var missing = ourPetIds.Except(mappedPetIds).ToList();
        if (missing.Count > 0)
            return BadRequest(new { error = "missing_mapping_for_pet", petIds = missing });
        var unknown = mappedPetIds.Except(ourPetIds).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = "unknown_pet", petIds = unknown });

        // No SPC pet may be mapped to two SnoutSpotter pets.
        var duplicateSpcIds = req.Mappings
            .Where(m => !string.IsNullOrEmpty(m.SpcPetId))
            .GroupBy(m => m.SpcPetId!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateSpcIds.Count > 0)
            return BadRequest(new { error = "duplicate_spc_pet_mapping", spcPetIds = duplicateSpcIds });

        if (!long.TryParse(req.SpcHouseholdId, out var parsedSpcHouseholdId))
            return BadRequest(new { error = "spc_household_id_invalid" });

        // Resolve the chosen SPC household name for display.
        List<SpcHouseholdResource> spcHouseholds;
        try
        {
            spcHouseholds = await _spc.ListHouseholdsAsync(active.AccessToken, ct);
        }
        catch (SpcUnauthorizedException)
        {
            return Unauthorized(new { error = "session_invalid" });
        }
        catch (SpcUpstreamException ex)
        {
            _log.LogWarning(ex, "SPC households fetch during link failed");
            return StatusCode(502, new { error = "upstream_unavailable" });
        }
        var chosen = spcHouseholds.FirstOrDefault(h => h.Id == parsedSpcHouseholdId);
        if (chosen == null)
            return BadRequest(new { error = "spc_household_not_accessible" });

        var now = DateTime.UtcNow.ToString("O");
        var secret = new SpcSecret(
            AccessToken: active.AccessToken,
            TokenType: "Bearer",
            IssuedAt: now,
            ClientUid: active.ClientUid,
            SpcUserId: active.SpcUserId,
            SpcUserEmail: active.SpcUserEmail);

        var secretArn = await _secrets.SaveAsync(householdId, secret, ct);

        var state = new SpcIntegrationState(
            Status: "linked",
            SpcHouseholdId: req.SpcHouseholdId,
            SpcHouseholdName: chosen.Name ?? $"Household {chosen.Id}",
            SpcUserEmail: active.SpcUserEmail,
            SecretArn: secretArn,
            LinkedAt: now,
            LastSyncAt: now,
            LastError: null);
        await _households.SaveAsync(householdId, state, ct);

        var mappedCount = await _petLinks.ApplyMappingsAsync(householdId, req.Mappings, ct);

        _sessions.Delete(active.SessionId, householdId);
        return Ok(new LinkResponse("linked", mappedCount));
    }

    [HttpDelete]
    public async Task<IActionResult> Unlink(CancellationToken ct)
    {
        var householdId = HttpContext.GetHouseholdId();
        await _petLinks.ClearAllAsync(householdId, ct);
        await _households.ClearAsync(householdId, ct);
        await _secrets.DeleteAsync(householdId, ct);
        return NoContent();
    }

    [HttpGet("devices")]
    public async Task<IActionResult> Devices(CancellationToken ct)
    {
        var householdId = HttpContext.GetHouseholdId();
        var state = await _households.GetAsync(householdId, ct);
        if (state == null || state.Status == "unlinked")
            return BadRequest(new { error = "not_linked" });
        if (state.Status == "token_expired")
            return Conflict(new { error = "token_expired" });

        var secret = await _secrets.GetAsync(householdId, ct);
        if (secret == null)
            return Conflict(new { error = "token_expired" });

        if (!long.TryParse(state.SpcHouseholdId, out var spcHouseholdId))
            return StatusCode(500, new { error = "invalid_spc_household_id" });

        try
        {
            var raw = await _spc.ListDevicesAsync(secret.AccessToken, spcHouseholdId, ct);
            var dto = raw
                .Select(d => new SpcDeviceDto(
                    Id: d.Id.ToString(),
                    ProductId: d.ProductId,
                    Name: d.Name ?? $"Device {d.Id}",
                    SerialNumber: d.SerialNumber,
                    LastActivityAt: d.LastActivityAt))
                .ToList();
            return Ok(new SpcDevicesResponse(dto));
        }
        catch (SpcUnauthorizedException)
        {
            await _households.MarkTokenExpiredAsync(householdId, ct);
            return Conflict(new { error = "token_expired" });
        }
        catch (SpcUpstreamException ex)
        {
            _log.LogWarning(ex, "SPC devices fetch upstream failure");
            return StatusCode(502, new { error = "upstream_unavailable" });
        }
    }

    [HttpPut("pet-links")]
    public async Task<IActionResult> UpdatePetLinks([FromBody] UpdatePetLinksRequest req, CancellationToken ct)
    {
        if (req.Mappings == null)
            return BadRequest(new { error = "mappings_required" });

        var householdId = HttpContext.GetHouseholdId();
        var state = await _households.GetAsync(householdId, ct);
        if (state == null || state.Status == "unlinked")
            return BadRequest(new { error = "not_linked" });

        var ourPetIds = (await _petLinks.ListPetIdsAsync(householdId, ct)).ToHashSet();
        var unknown = req.Mappings.Select(m => m.PetId).Where(id => !ourPetIds.Contains(id)).ToList();
        if (unknown.Count > 0)
            return BadRequest(new { error = "unknown_pet", petIds = unknown });

        var duplicateSpcIds = req.Mappings
            .Where(m => !string.IsNullOrEmpty(m.SpcPetId))
            .GroupBy(m => m.SpcPetId!)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicateSpcIds.Count > 0)
            return BadRequest(new { error = "duplicate_spc_pet_mapping", spcPetIds = duplicateSpcIds });

        var updated = await _petLinks.ApplyMappingsAsync(householdId, req.Mappings, ct);
        return Ok(new UpdatePetLinksResponse(updated));
    }

    private (SpcSession? Session, IActionResult? Error) ResolveSession(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return (null, BadRequest(new { error = "session_required" }));
        var householdId = HttpContext.GetHouseholdId();
        var session = _sessions.Get(sessionId, householdId);
        if (session == null)
            return (null, StatusCode(410, new { error = "session_expired" }));
        return (session, null);
    }
}
