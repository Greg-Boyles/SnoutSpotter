using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace SnoutSpotter.Api.Services;

public class S3PresignService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private const int DefaultExpirySeconds = 3600;

    public S3PresignService(IAmazonS3 s3Client, IOptions<AppConfig> config)
    {
        _s3Client = s3Client;
        _bucketName = config.Value.BucketName;
    }

    public string GeneratePresignedUrl(string s3Key, int expirySeconds = DefaultExpirySeconds)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
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
