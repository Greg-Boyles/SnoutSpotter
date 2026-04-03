namespace SnoutSpotter.Lambda.RunInference;

public record S3EventDetail(S3Bucket Bucket, S3Object Object, string Reason);