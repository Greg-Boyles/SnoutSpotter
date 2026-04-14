using Amazon.DynamoDBv2;
using Amazon.IoT;
using Amazon.IotData;
using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api;

public static class StatsRefreshRunner
{
    public static async Task RunAsync()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.Configure<AppConfig>(cfg =>
                {
                    cfg.ClipsTable = Environment.GetEnvironmentVariable("TABLE_NAME") ?? "snout-spotter-clips";
                    cfg.LabelsTable = Environment.GetEnvironmentVariable("LABELS_TABLE") ?? "snout-spotter-labels";
                    cfg.StatsTable = Environment.GetEnvironmentVariable("STATS_TABLE") ?? "snout-spotter-stats";
                    cfg.IoTThingGroup = Environment.GetEnvironmentVariable("IOT_THING_GROUP") ?? "snoutspotter-pis";
                });

                services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
                services.AddSingleton<IAmazonS3, AmazonS3Client>();
                services.AddSingleton<IAmazonIoT>(_ => new AmazonIoTClient(Amazon.RegionEndpoint.EUWest1));
                services.AddSingleton<IAmazonIotData>(sp =>
                {
                    var iot = sp.GetRequiredService<IAmazonIoT>();
                    var endpoint = iot.DescribeEndpointAsync(new Amazon.IoT.Model.DescribeEndpointRequest
                    {
                        EndpointType = "iot:Data-ATS"
                    }).GetAwaiter().GetResult();
                    return new AmazonIotDataClient(new AmazonIotDataConfig
                    {
                        ServiceURL = $"https://{endpoint.EndpointAddress}"
                    });
                });

                services.AddSingleton<IS3UrlService, S3UrlService>();
                services.AddSingleton<IClipService, ClipService>();
                services.AddSingleton<ILabelService, LabelService>();
                services.AddSingleton<IPiUpdateService, PiUpdateService>();
                services.AddSingleton<IStatsRefreshService>(sp => new StatsRefreshService(
                    sp.GetRequiredService<IAmazonDynamoDB>(),
                    sp.GetRequiredService<IClipService>(),
                    sp.GetRequiredService<ILabelService>(),
                    sp.GetRequiredService<IPiUpdateService>(),
                    sp.GetRequiredService<IOptions<AppConfig>>()));
            })
            .Build();

        var service = host.Services.GetRequiredService<IStatsRefreshService>();
        await service.RefreshAllAsync();
    }
}
