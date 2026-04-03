namespace SnoutSpotter.Api.Models;

public record PresignedUrlResponse(string Url, int ExpiresInSeconds);