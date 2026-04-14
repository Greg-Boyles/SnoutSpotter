namespace SnoutSpotter.Api.Models;

public record BulkConfirmRequest(List<string> KeyframeKeys, string ConfirmedLabel, string? Breed = null);