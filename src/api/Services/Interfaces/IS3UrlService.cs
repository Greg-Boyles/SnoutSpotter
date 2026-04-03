namespace SnoutSpotter.Api.Services.Interfaces;

public interface IS3UrlService
{
    string GetPresignedUrl(string key, TimeSpan expiration);
    List<string> GetPresignedUrls(IEnumerable<string> keys, TimeSpan expiration);
}
