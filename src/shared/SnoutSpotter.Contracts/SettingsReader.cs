using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace SnoutSpotter.Contracts;

/// <summary>
/// Reads server settings from DynamoDB with in-memory caching.
/// Shared by all Lambda functions. Cache TTL = 5 minutes.
/// Falls back to ServerSettings.Defaults if key not in table.
/// </summary>
public class SettingsReader
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;
    private Dictionary<string, string>? _cache;
    private DateTime _cacheExpiry;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    public SettingsReader(IAmazonDynamoDB dynamoDb, string? tableName = null)
    {
        _dynamoDb = dynamoDb;
        _tableName = tableName ?? Environment.GetEnvironmentVariable("SETTINGS_TABLE") ?? "snout-spotter-settings";
    }

    public async Task<int> GetIntAsync(string key, int? defaultOverride = null)
    {
        var value = await GetStringAsync(key);
        return int.TryParse(value, out var result) ? result : defaultOverride ?? int.Parse(ServerSettings.GetDefault(key));
    }

    public async Task<float> GetFloatAsync(string key, float? defaultOverride = null)
    {
        var value = await GetStringAsync(key);
        return float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : defaultOverride ?? float.Parse(ServerSettings.GetDefault(key), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<string> GetStringAsync(string key)
    {
        var all = await GetAllAsync();
        return all.TryGetValue(key, out var value) ? value : ServerSettings.GetDefault(key);
    }

    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        if (_cache != null && DateTime.UtcNow < _cacheExpiry)
            return _cache;

        var settings = new Dictionary<string, string>();
        try
        {
            var response = await _dynamoDb.ScanAsync(new ScanRequest
            {
                TableName = _tableName,
                ProjectionExpression = "setting_key, setting_value"
            });

            foreach (var item in response.Items)
            {
                var key = item.GetValueOrDefault("setting_key")?.S;
                var val = item.GetValueOrDefault("setting_value")?.S;
                if (key != null && val != null)
                    settings[key] = val;
            }
        }
        catch
        {
            // Fall back to defaults if DynamoDB read fails
        }

        _cache = settings;
        _cacheExpiry = DateTime.UtcNow.Add(CacheTtl);
        return settings;
    }
}
