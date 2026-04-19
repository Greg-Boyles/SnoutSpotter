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
    private readonly ILogger<SpcIntegrationController> _log;

    public SpcIntegrationController(ISpcApiClient spc, ISpcSessionStore sessions, ILogger<SpcIntegrationController> log)
    {
        _spc = spc;
        _sessions = sessions;
        _log = log;
    }

    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var householdId = HttpContext.GetHouseholdId();
        // Link persistence lands in PR3; until then every household reads as unlinked.
        return Ok(new { status = "unlinked", householdId });
    }

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] ValidateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new { error = "email_and_password_required" });

        // Fresh random client_uid for now — in PR3 (persist flow) we'll reuse the
        // one stored alongside the token in Secrets Manager if re-linking, so SPC
        // sees a stable API client identifier per household.
        var clientUid = Guid.NewGuid().ToString();
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
