namespace SnoutSpotter.Api.Services;

public interface IS3UrlService
{
    string GetPresignedUrl(string key, TimeSpan expiration);
    List<string> GetPresignedUrls(IEnumerable<string> keys, TimeSpan expiration);
}
