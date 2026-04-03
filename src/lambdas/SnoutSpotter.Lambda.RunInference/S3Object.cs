namespace SnoutSpotter.Lambda.RunInference;

public record S3Object(string Key, long Size, string ETag, string VersionId);