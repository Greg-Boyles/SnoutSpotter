using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
        var pets = await _petService.ListAsync();
        return Ok(pets);
    }

    [HttpGet("{petId}")]
    public async Task<IActionResult> Get(string petId)
    {
        var pet = await _petService.GetAsync(petId);
        if (pet is null) return NotFound();
        return Ok(pet);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Pet name is required.");

        var pet = await _petService.CreateAsync(request);
        return CreatedAtAction(nameof(Get), new { petId = pet.PetId }, pet);
    }

    [HttpPut("{petId}")]
    public async Task<IActionResult> Update(string petId, [FromBody] UpdatePetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return BadRequest("Pet name is required.");

        try
        {
            var pet = await _petService.UpdateAsync(petId, request);
            return Ok(pet);
        }
        catch (Amazon.DynamoDBv2.Model.ConditionalCheckFailedException)
        {
            return NotFound();
        }
    }

    [HttpPost("migrate")]
    public async Task<IActionResult> Migrate([FromBody] MigratePetRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PetId))
            return BadRequest(new { error = "petId is required" });

        try
        {
            var result = await _petService.MigrateLegacyLabelsAsync(request.PetId);
            return Ok(new
            {
                message = $"Migration complete: {result.LabelsUpdated} labels, {result.ClipsUpdated} clips updated",
                labelsUpdated = result.LabelsUpdated,
                clipsUpdated = result.ClipsUpdated
            });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{petId}")]
    public async Task<IActionResult> Delete(string petId)
    {
        try
        {
            await _petService.DeleteAsync(petId);
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
