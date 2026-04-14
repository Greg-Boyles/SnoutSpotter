namespace SnoutSpotter.Api.Models;

public record RerunInferenceRequest(string? DateFrom = null, string? DateTo = null, List<string>? ClipIds = null);