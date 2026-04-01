using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;

namespace SnoutSpotter.Api.Services;

public class HealthService : IHealthService
{
    private readonly IAmazonCloudWatch _cloudWatchClient;

    public HealthService(IAmazonCloudWatch cloudWatchClient)
    {
        _cloudWatchClient = cloudWatchClient;
    }

    public async Task<bool> IsPiOnlineAsync()
    {
        var response = await _cloudWatchClient.GetMetricDataAsync(new GetMetricDataRequest
        {
            StartTimeUtc = DateTime.UtcNow.AddMinutes(-15),
            EndTimeUtc = DateTime.UtcNow,
            MetricDataQueries = new List<MetricDataQuery>
            {
                new()
                {
                    Id = "heartbeat",
                    MetricStat = new MetricStat
                    {
                        Metric = new Metric
                        {
                            Namespace = "SnoutSpotter",
                            MetricName = "PiHeartbeat"
                        },
                        Period = 300,
                        Stat = "Sum"
                    }
                }
            }
        });

        var values = response.MetricDataResults.FirstOrDefault()?.Values;
        return values != null && values.Any(v => v > 0);
    }
}
