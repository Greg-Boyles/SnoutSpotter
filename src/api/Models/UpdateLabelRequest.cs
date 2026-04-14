namespace SnoutSpotter.Api.Models;

public record UpdateLabelRequest(string ConfirmedLabel, string? Breed = null);