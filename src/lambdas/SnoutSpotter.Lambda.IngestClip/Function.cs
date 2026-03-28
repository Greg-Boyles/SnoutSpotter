using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SnoutSpotter.Lambda.IngestClip;

public class Function
{
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoClient;
    private readonly string _bucketName;
    private readonly string _tableName;

    public Function()
    {
        _s3Client = new AmazonS3Client();
        _dynamoClient = new AmazonDynamoDBClient();
        _bucketName = Environment.GetEnvironmentVariable("BUCKET_NAME")
            ?? throw new InvalidOperationException("BUCKET_NAME not set");
        _tableName = Environment.GetEnvironmentVariable("TABLE_NAME")
            ?? throw new InvalidOperationException("TABLE_NAME not set");
    }

    // Constructor for testing
    public Function(IAmazonS3 s3Client, IAmazonDynamoDB dynamoClient, string bucketName, string tableName)
    {
        _s3Client = s3Client;
        _dynamoClient = dynamoClient;
        _bucketName = bucketName;
        _tableName = tableName;
    }

    public async Task FunctionHandler(EventBridgeEvent<S3EventDetail> eventBridgeEvent, ILambdaContext context)
    {
        var detail = eventBridgeEvent.Detail;
        var s3Key = detail.Object.Key;
        
        context.Logger.LogInformation($"Processing: {s3Key}");

        // Parse metadata from key: raw-clips/YYYY/MM/DD/timestamp_durations.mp4
        var (clipId, timestamp, durationSeconds, date) = ParseS3Key(s3Key);

        // Download the video to /tmp for keyframe extraction
        var localVideoPath = $"/tmp/{clipId}.mp4";
        await DownloadFromS3(s3Key, localVideoPath);

        // Extract keyframes using FFmpeg (1 frame per 5 seconds)
        var keyframePaths = await ExtractKeyframes(localVideoPath, clipId, context);

        // Upload keyframes to S3
        var keyframeKeys = new List<string>();
        foreach (var keyframePath in keyframePaths)
        {
            var frameNum = Path.GetFileNameWithoutExtension(keyframePath).Split('_').Last();
            var keyframeKey = $"keyframes/{date}/{clipId}_{frameNum}.jpg";
            await UploadToS3(keyframePath, keyframeKey);
            keyframeKeys.Add(keyframeKey);
            File.Delete(keyframePath);
        }

        // Write metadata to DynamoDB
        await WriteClipMetadata(clipId, s3Key, timestamp, durationSeconds, date, keyframeKeys);

        // Cleanup
        File.Delete(localVideoPath);

        context.Logger.LogInformation(
            $"Ingested clip {clipId}: {keyframeKeys.Count} keyframes extracted");
    }

    private static (string clipId, long timestamp, int durationSeconds, string date) ParseS3Key(string key)
    {
        // Expected format: raw-clips/YYYY/MM/DD/2025-01-15T14-30-00_45s.mp4
        var fileName = Path.GetFileNameWithoutExtension(key);
        var parts = fileName.Split('_');

        var timestampStr = parts[0]; // ISO-ish timestamp
        var durationStr = parts.Length > 1 ? parts[1].TrimEnd('s') : "0";

        var clipId = fileName;
        int.TryParse(durationStr, out var durationSeconds);

        // Extract date from path
        var pathParts = key.Split('/');
        var date = pathParts.Length >= 4
            ? $"{pathParts[1]}/{pathParts[2]}/{pathParts[3]}"
            : DateTime.UtcNow.ToString("yyyy/MM/dd");

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (DateTime.TryParse(timestampStr.Replace('-', ':'), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var parsed))
        {
            timestamp = new DateTimeOffset(parsed, TimeSpan.Zero).ToUnixTimeSeconds();
        }

        return (clipId, timestamp, durationSeconds, date);
    }

    private async Task DownloadFromS3(string key, string localPath)
    {
        var response = await _s3Client.GetObjectAsync(_bucketName, key);
        await using var fileStream = File.Create(localPath);
        await response.ResponseStream.CopyToAsync(fileStream);
    }

    private async Task UploadToS3(string localPath, string key)
    {
        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucketName,
            Key = key,
            FilePath = localPath,
            ContentType = "image/jpeg"
        });
    }

    private static async Task<List<string>> ExtractKeyframes(
        string videoPath, string clipId, ILambdaContext context)
    {
        var outputPattern = $"/tmp/{clipId}_%04d.jpg";

        // Extract 1 frame every 5 seconds
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-i \"{videoPath}\" -vf \"fps=1/5\" -q:v 2 \"{outputPattern}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            context.Logger.LogWarning($"FFmpeg stderr: {stderr}");
        }

        // Collect output files
        var keyframes = Directory.GetFiles("/tmp", $"{clipId}_*.jpg")
            .OrderBy(f => f)
            .ToList();

        context.Logger.LogInformation($"Extracted {keyframes.Count} keyframes from {videoPath}");
        return keyframes;
    }

    private async Task WriteClipMetadata(
        string clipId, string s3Key, long timestamp, int durationSeconds,
        string date, List<string> keyframeKeys)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["clip_id"] = new() { S = clipId },
            ["pk"] = new() { S = "CLIP" },
            ["s3_key"] = new() { S = s3Key },
            ["timestamp"] = new() { N = timestamp.ToString() },
            ["duration_s"] = new() { N = durationSeconds.ToString() },
            ["date"] = new() { S = date },
            ["keyframe_count"] = new() { N = keyframeKeys.Count.ToString() },
            ["keyframe_keys"] = new() { SS = keyframeKeys },
            ["labeled"] = new() { BOOL = false },
            ["detection_type"] = new() { S = "pending" },
            ["created_at"] = new() { S = DateTime.UtcNow.ToString("O") }
        };

        await _dynamoClient.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = item
        });
    }
}

public record EventBridgeEvent<T>(string Version, string Id, string DetailType, string Source, string Account, string Time, string Region, T Detail);

public record S3EventDetail(Bucket Bucket, S3Object Object, string Reason);

public record Bucket(string Name);

public record S3Object(string Key, long Size, string ETag, string VersionId);
