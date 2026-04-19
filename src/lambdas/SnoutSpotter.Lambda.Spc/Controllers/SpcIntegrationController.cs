using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Lambda.Spc.Extensions;

namespace SnoutSpotter.Lambda.Spc.Controllers;

[ApiController]
[Route("api/integrations/spc")]
[Authorize]
public class SpcIntegrationController : ControllerBase
{
    [HttpGet("status")]
    public IActionResult GetStatus()
    {
        var householdId = HttpContext.GetHouseholdId();
        return Ok(new { status = "unlinked", householdId });
    }
}
