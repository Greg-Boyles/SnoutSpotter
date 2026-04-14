namespace SnoutSpotter.Api.Models;

public record UpdateBoundingBoxesRequest(string KeyframeKey, List<float[]> Boxes);