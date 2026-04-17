using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PetsController : ControllerBase
{
    private readonly IPetService _petService;

    public PetsController(IPetService petService)
    {
        _petService = petService;
    }

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var pets = await _petService.ListAsync(HttpContext.GetHouseholdId());
        return Ok(pets);
    }

    [HttpGet("{petId}")]
    public async Task<IActionResult> Get(string petId)
    {
        var pet = await _petService.GetAsync(petId, HttpContext.GetHouseholdId());
        if (pet is null) return NotFound();
        return Ok(pet);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Pet name is required.");

        var pet = await _petService.CreateAsync(request, HttpContext.GetHouseholdId());
        return CreatedAtAction(nameof(Get), new { petId = pet.PetId }, pet);
    }

    [HttpPut("{petId}")]
    public async Task<IActionResult> Update(string petId, [FromBody] UpdatePetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Pet name is required.");

        try
        {
            var pet = await _petService.UpdateAsync(petId, request, HttpContext.GetHouseholdId());
            return Ok(pet);
        }
        catch (Amazon.DynamoDBv2.Model.ConditionalCheckFailedException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{petId}")]
    public async Task<IActionResult> Delete(string petId)
    {
        try
        {
            await _petService.DeleteAsync(petId, HttpContext.GetHouseholdId());
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Amazon.DynamoDBv2.Model.ConditionalCheckFailedException)
        {
            return NotFound();
        }
    }
}
