using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.KinesisVideo;
using Amazon.KinesisVideo.Model;
using Amazon.KinesisVideoSignalingChannels;
using Amazon.KinesisVideoSignalingChannels.Model;
using Amazon.Runtime;

namespace SnoutSpotter.Api.Services;

public class StreamService
{
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonKinesisVideo _kvs;
    private const string Region = "eu-west-1";
    private const string Service = "kinesisvideo";

    public StreamService(IAmazonIotData iotData, IAmazonKinesisVideo kvs)
    {
        _iotData = iotData;
        _kvs = kvs;
    }

    public async Task<StreamStartResult> StartStreamAsync(string thingName)
    {
        var channelName = $"snoutspotter-{thingName.Replace("snoutspotter-", "")}-live";

        // Ensure signaling channel exists
        string channelArn;
        try
        {
            var desc = await _kvs.DescribeSignalingChannelAsync(new DescribeSignalingChannelRequest
            {
                ChannelName = channelName
            });
            channelArn = desc.ChannelInfo.ChannelARN;
        }
        catch (Amazon.KinesisVideo.Model.ResourceNotFoundException)
        {
            var create = await _kvs.CreateSignalingChannelAsync(new CreateSignalingChannelRequest
            {
                ChannelName = channelName,
                ChannelType = ChannelType.SINGLE_MASTER,
                SingleMasterConfiguration = new SingleMasterConfiguration
                {
                    MessageTtlSeconds = 60
                }
            });
            channelArn = create.ChannelARN;
        }

        // Write desired.streaming = true to shadow
        var shadowPayload = JsonSerializer.Serialize(new
        {
            state = new { desired = new { streaming = true } }
        });
        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(Encoding.UTF8.GetBytes(shadowPayload))
        });

        // Get signaling channel endpoints for the viewer
        var endpoints = await _kvs.GetSignalingChannelEndpointAsync(new GetSignalingChannelEndpointRequest
        {
            ChannelARN = channelArn,
            SingleMasterChannelEndpointConfiguration = new SingleMasterChannelEndpointConfiguration
            {
                Protocols = new List<string> { "WSS", "HTTPS" },
                Role = ChannelRole.VIEWER
            }
        });

        var wssEndpoint = endpoints.ResourceEndpointList.FirstOrDefault(e => e.Protocol == "WSS")?.ResourceEndpoint;
        var httpsEndpoint = endpoints.ResourceEndpointList.FirstOrDefault(e => e.Protocol == "HTTPS")?.ResourceEndpoint;

        // Get ICE server config
        List<IceServerInfo>? iceServers = null;
        if (httpsEndpoint != null)
        {
            var signalingClient = new AmazonKinesisVideoSignalingChannelsClient(
                new AmazonKinesisVideoSignalingChannelsConfig { ServiceURL = httpsEndpoint });
            var iceResponse = await signalingClient.GetIceServerConfigAsync(new GetIceServerConfigRequest
            {
                ChannelARN = channelArn
            });
            iceServers = iceResponse.IceServerList.Select(s => new IceServerInfo
            {
                Urls = s.Uris,
                Username = s.Username,
                Credential = s.Password,
                Ttl = s.Ttl
            }).ToList();
        }

        // Create presigned WSS URL for the viewer
        string? presignedWssUrl = null;
        if (wssEndpoint != null)
        {
            var credentials = await FallbackCredentialsFactory.GetCredentials().GetCredentialsAsync();
            var clientId = $"viewer-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
            presignedWssUrl = CreatePresignedUrl(
                wssEndpoint, channelArn, clientId, credentials, Region);
        }

        return new StreamStartResult
        {
            ChannelName = channelName,
            ChannelArn = channelArn,
            Region = Region,
            PresignedWssUrl = presignedWssUrl,
            IceServers = iceServers
        };
    }

    public async Task StopStreamAsync(string thingName)
    {
        var payload = JsonSerializer.Serialize(new
        {
            state = new { desired = new { streaming = false } }
        });
        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(Encoding.UTF8.GetBytes(payload))
        });
    }

    private static string CreatePresignedUrl(
        string wssEndpoint, string channelArn, string clientId,
        ImmutableCredentials credentials, string region)
    {
        var uri = new Uri(wssEndpoint);
        var host = uri.Host;
        var now = DateTime.UtcNow;
        var datestamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var credentialScope = $"{datestamp}/{region}/{Service}/aws4_request";

        var queryParams = new SortedDictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-ChannelARN"] = channelArn,
            ["X-Amz-ClientId"] = clientId,
            ["X-Amz-Credential"] = $"{credentials.AccessKey}/{credentialScope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = "300",
            ["X-Amz-SignedHeaders"] = "host"
        };

        if (!string.IsNullOrEmpty(credentials.Token))
            queryParams["X-Amz-Security-Token"] = credentials.Token;

        var canonicalQueryString = string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var canonicalRequest = $"GET\n/\n{canonicalQueryString}\nhost:{host}\n\nhost\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var stringToSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n" +
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = GetSignatureKey(credentials.SecretKey, datestamp, region, Service);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return $"wss://{host}/?{canonicalQueryString}&X-Amz-Signature={signature}";
    }

    private static byte[] GetSignatureKey(string key, string datestamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{key}"), Encoding.UTF8.GetBytes(datestamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}

public class StreamStartResult
{
    public required string ChannelName { get; init; }
    public required string ChannelArn { get; init; }
    public required string Region { get; init; }
    public string? PresignedWssUrl { get; init; }
    public List<IceServerInfo>? IceServers { get; init; }
}

public class IceServerInfo
{
    public List<string>? Urls { get; init; }
    public string? Username { get; init; }
    public string? Credential { get; init; }
    public int Ttl { get; init; }
}
