namespace SnoutSpotter.Api.Services;

public interface IS3PresignService
{
    string GeneratePresignedUrl(string s3Key, int expirySeconds = 3600);
    string GeneratePresignedPutUrl(string s3Key, string contentType, int expirySeconds = 3600);
    List<string> GeneratePresignedUrls(IEnumerable<string> s3Keys, int expirySeconds = 3600);
}
