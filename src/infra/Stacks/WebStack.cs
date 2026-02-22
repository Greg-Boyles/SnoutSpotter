using Amazon.CDK;
using Amazon.CDK.AWS.CloudFront;
using Amazon.CDK.AWS.CloudFront.Origins;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace SnoutSpotter.Infra.Stacks;

public class WebStack : Stack
{
    public WebStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        // S3 bucket for static frontend assets
        var websiteBucket = new Bucket(this, "WebsiteBucket", new BucketProps
        {
            BucketName = $"snout-spotter-web-{Account}",
            RemovalPolicy = RemovalPolicy.DESTROY,
            AutoDeleteObjects = true,
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL
        });

        // CloudFront distribution
        var distribution = new Distribution(this, "WebDistribution", new DistributionProps
        {
            DefaultBehavior = new BehaviorOptions
            {
                Origin = S3BucketOrigin.WithOriginAccessControl(websiteBucket),
                ViewerProtocolPolicy = ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
                CachePolicy = CachePolicy.CACHING_OPTIMIZED
            },
            DefaultRootObject = "index.html",
            ErrorResponses = new[]
            {
                // SPA: route all 404s to index.html for client-side routing
                new ErrorResponse
                {
                    HttpStatus = 404,
                    ResponseHttpStatus = 200,
                    ResponsePagePath = "/index.html",
                    Ttl = Duration.Seconds(0)
                },
                new ErrorResponse
                {
                    HttpStatus = 403,
                    ResponseHttpStatus = 200,
                    ResponsePagePath = "/index.html",
                    Ttl = Duration.Seconds(0)
                }
            }
        });

        // Outputs
        _ = new CfnOutput(this, "WebsiteBucketName", new CfnOutputProps
        {
            Value = websiteBucket.BucketName,
            Description = "S3 bucket for frontend assets"
        });

        _ = new CfnOutput(this, "DistributionDomainName", new CfnOutputProps
        {
            Value = distribution.DistributionDomainName,
            Description = "CloudFront distribution URL"
        });

        _ = new CfnOutput(this, "DistributionId", new CfnOutputProps
        {
            Value = distribution.DistributionId,
            Description = "CloudFront distribution ID (for cache invalidation)"
        });
    }
}
