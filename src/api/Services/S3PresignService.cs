using Amazon.S3;
using Amazon.S3.Model;

namespace SnoutSpotter.Api.Services;

public class S3PresignService
{
    private readonly IAmazonS3 _s3Client;
    private const string BucketName = "snout-spotter"; // Will be suffixed with account ID at runtime
    private const int DefaultExpirySeconds = 3600;

    public S3PresignService(IAmazonS3 s3Client)
    {
        _s3Client = s3Client;
    }

    public string GeneratePresignedUrl(string s3Key, int expirySeconds = DefaultExpirySeconds)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Environment.GetEnvironmentVariable("BUCKET_NAME") ?? BucketName,
            Key = s3Key,
            Expires = DateTime.UtcNow.AddSeconds(expirySeconds),
            Verb = HttpVerb.GET
        };

        return _s3Client.GetPreSignedURL(request);
    }

    public List<string> GeneratePresignedUrls(IEnumerable<string> s3Keys, int expirySeconds = DefaultExpirySeconds)
    {
        return s3Keys.Select(key => GeneratePresignedUrl(key, expirySeconds)).ToList();
    }
}
