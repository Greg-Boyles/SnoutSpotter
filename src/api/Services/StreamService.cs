using System.Text.Json;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.KinesisVideo;
using Amazon.KinesisVideo.Model;
using Amazon.KinesisVideoSignalingChannels;
using Amazon.KinesisVideoSignalingChannels.Model;

namespace SnoutSpotter.Api.Services;

public class StreamService
{
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonKinesisVideo _kvs;
    private readonly IAmazonKinesisVideoSignalingChannels _kvsSignaling;

    public StreamService(
        IAmazonIotData iotData,
        IAmazonKinesisVideo kvs,
        IAmazonKinesisVideoSignalingChannels kvsSignaling)
    {
        _iotData = iotData;
        _kvs = kvs;
        _kvsSignaling = kvsSignaling;
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
        var payload = JsonSerializer.Serialize(new
        {
            state = new { desired = new { streaming = true } }
        });
        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
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
        List<IceServer>? iceServers = null;
        if (httpsEndpoint != null)
        {
            var signalingClient = new AmazonKinesisVideoSignalingChannelsClient(
                new AmazonKinesisVideoSignalingChannelsConfig { ServiceURL = httpsEndpoint });
            var iceResponse = await signalingClient.GetIceServerConfigAsync(new GetIceServerConfigRequest
            {
                ChannelARN = channelArn
            });
            iceServers = iceResponse.IceServerList;
        }

        return new StreamStartResult
        {
            ChannelName = channelName,
            ChannelArn = channelArn,
            Region = "eu-west-1",
            WssEndpoint = wssEndpoint,
            HttpsEndpoint = httpsEndpoint,
            IceServers = iceServers?.Select(s => new IceServerInfo
            {
                Urls = s.Uris,
                Username = s.Username,
                Credential = s.Password,
                Ttl = s.Ttl
            }).ToList()
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
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(payload))
        });
    }
}

public class StreamStartResult
{
    public required string ChannelName { get; init; }
    public required string ChannelArn { get; init; }
    public required string Region { get; init; }
    public string? WssEndpoint { get; init; }
    public string? HttpsEndpoint { get; init; }
    public List<IceServerInfo>? IceServers { get; init; }
}

public class IceServerInfo
{
    public List<string>? Urls { get; init; }
    public string? Username { get; init; }
    public string? Credential { get; init; }
    public int Ttl { get; init; }
}
