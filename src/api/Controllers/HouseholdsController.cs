using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/households")]
[Authorize]
public class HouseholdsController : ControllerBase
{
    private readonly IHouseholdService _householdService;
    private readonly IUserService _userService;

    public HouseholdsController(IHouseholdService householdService, IUserService userService)
    {
        _householdService = householdService;
        _userService = userService;
    }

    [HttpGet]
    public async Task<ActionResult> ListHouseholds()
    {
        var userId = HttpContext.GetUserId();
        var user = await _userService.GetOrCreateAsync(userId, HttpContext.User);
        var households = await _householdService.GetForUserAsync(user);
        return Ok(new { households, userId = user.UserId, email = user.Email, name = user.Name });
    }

    [HttpPost]
    public async Task<ActionResult> CreateHousehold([FromBody] CreateHouseholdRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest(new { error = "Name is required" });

        var userId = HttpContext.GetUserId();
        var household = await _householdService.CreateAsync(request.Name, userId);
        _userService.InvalidateCache(userId);
        return Ok(household);
    }
}
