namespace SnoutSpotter.Api.Models;

public record BackfillBoxesRequest(string? ConfirmedLabel = null, List<string>? Keys = null);