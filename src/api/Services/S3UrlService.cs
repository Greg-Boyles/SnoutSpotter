using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace SnoutSpotter.Api.Services;

public class S3UrlService : IS3UrlService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;

    public S3UrlService(IAmazonS3 s3Client, IOptions<AppConfig> config)
    {
        _s3Client = s3Client;
        _bucketName = config.Value.BucketName;
    }

    public string GetPresignedUrl(string key, TimeSpan expiration)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucketName,
            Key = key,
            Expires = DateTime.UtcNow.Add(expiration)
        };

        return _s3Client.GetPreSignedURL(request);
    }

    public List<string> GetPresignedUrls(IEnumerable<string> keys, TimeSpan expiration)
    {
        return keys.Select(key => GetPresignedUrl(key, expiration)).ToList();
    }
}
