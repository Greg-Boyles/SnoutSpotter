using System.Text.Json;
using Amazon.IotData;
using Amazon.IotData.Model;
using Amazon.KinesisVideo;
using Amazon.KinesisVideo.Model;

namespace SnoutSpotter.Api.Services;

public class StreamService : IStreamService
{
    private readonly IAmazonIotData _iotData;
    private readonly IAmazonKinesisVideo _kvs;

    public StreamService(IAmazonIotData iotData, IAmazonKinesisVideo kvs)
    {
        _iotData = iotData;
        _kvs = kvs;
    }

    public async Task<StreamStartResult> StartStreamAsync(string thingName)
    {
        var streamName = $"snoutspotter-{thingName.Replace("snoutspotter-", "")}-live";

        // Write desired.streaming = true to shadow — Pi agent will start kvssink
        var shadowPayload = JsonSerializer.Serialize(new
        {
            state = new { desired = new { streaming = true } }
        });
        await _iotData.UpdateThingShadowAsync(new UpdateThingShadowRequest
        {
            ThingName = thingName,
            Payload = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(shadowPayload))
        });

        return new StreamStartResult
        {
            StreamName = streamName,
            Region = "eu-west-1"
        };
    }

    public async Task<string?> GetHlsUrlAsync(string thingName)
    {
        var streamName = $"snoutspotter-{thingName.Replace("snoutspotter-", "")}-live";

        try
        {
            // Get the data endpoint for HLS
            var endpointResponse = await _kvs.GetDataEndpointAsync(new GetDataEndpointRequest
            {
                StreamName = streamName,
                APIName = APIName.GET_HLS_STREAMING_SESSION_URL
            });

            // Create an archived media client pointed at the data endpoint
            var archivedMediaClient = new Amazon.KinesisVideoArchivedMedia.AmazonKinesisVideoArchivedMediaClient(
                new Amazon.KinesisVideoArchivedMedia.AmazonKinesisVideoArchivedMediaConfig
                {
                    ServiceURL = endpointResponse.DataEndpoint
                });

            var hlsResponse = await archivedMediaClient.GetHLSStreamingSessionURLAsync(
                new Amazon.KinesisVideoArchivedMedia.Model.GetHLSStreamingSessionURLRequest
                {
                    StreamName = streamName,
                    PlaybackMode = Amazon.KinesisVideoArchivedMedia.HLSPlaybackMode.LIVE,
                    HLSFragmentSelector = new Amazon.KinesisVideoArchivedMedia.Model.HLSFragmentSelector
                    {
                        FragmentSelectorType = Amazon.KinesisVideoArchivedMedia.HLSFragmentSelectorType.PRODUCER_TIMESTAMP
                    },
                    ContainerFormat = Amazon.KinesisVideoArchivedMedia.ContainerFormat.FRAGMENTED_MP4,
                    DiscontinuityMode = Amazon.KinesisVideoArchivedMedia.HLSDiscontinuityMode.ALWAYS,
                    DisplayFragmentTimestamp = Amazon.KinesisVideoArchivedMedia.HLSDisplayFragmentTimestamp.ALWAYS,
                    Expires = 300
                });

            return hlsResponse.HLSStreamingSessionURL;
        }
        catch (Amazon.KinesisVideo.Model.ResourceNotFoundException)
        {
            return null; // Stream not created yet (Pi hasn't started kvssink)
        }
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
    public required string StreamName { get; init; }
    public required string Region { get; init; }
}
