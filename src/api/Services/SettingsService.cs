using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Services.Interfaces;
using SnoutSpotter.Contracts;

namespace SnoutSpotter.Api.Services;

public class SettingsService : ISettingsService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public SettingsService(IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _tableName = config.Value.SettingsTable;
    }

    public async Task<Dictionary<string, SettingValue>> GetAllAsync()
    {
        // Read current values from DynamoDB
        var stored = new Dictionary<string, string>();
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
                stored[key] = val;
        }

        // Merge with defaults
        var result = new Dictionary<string, SettingValue>();
        foreach (var (key, spec) in ServerSettings.All)
        {
            var currentValue = stored.TryGetValue(key, out var v) ? v : spec.Default;
            result[key] = new SettingValue(key, currentValue, spec.Default, spec.Label, spec.Type, spec.Min, spec.Max, spec.Description, spec.Options);
        }

        return result;
    }

    public async Task UpdateAsync(string key, string value)
    {
        if (!ServerSettings.All.TryGetValue(key, out var spec))
            throw new ArgumentException($"Unknown setting: {key}");

        // Validate
        if (spec.Type == "int")
        {
            if (!int.TryParse(value, out var intVal))
                throw new ArgumentException($"{key} must be an integer");
            if (intVal < spec.Min || intVal > spec.Max)
                throw new ArgumentException($"{key} must be between {spec.Min} and {spec.Max}");
        }
        else if (spec.Type == "float")
        {
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floatVal))
                throw new ArgumentException($"{key} must be a number");
            if (floatVal < spec.Min || floatVal > spec.Max)
                throw new ArgumentException($"{key} must be between {spec.Min} and {spec.Max}");
        }
        else if (spec.Type == "select")
        {
            if (spec.Options == null || !spec.Options.Contains(value))
                throw new ArgumentException($"{key} must be one of: {string.Join(", ", spec.Options ?? Array.Empty<string>())}");
        }

        await _dynamoDb.PutItemAsync(_tableName, new Dictionary<string, AttributeValue>
        {
            ["setting_key"] = new() { S = key },
            ["setting_value"] = new() { S = value }
        });
    }

    public async Task ResetAllAsync()
    {
        // Delete all items from the settings table
        var response = await _dynamoDb.ScanAsync(new ScanRequest
        {
            TableName = _tableName,
            ProjectionExpression = "setting_key"
        });

        foreach (var item in response.Items)
        {
            await _dynamoDb.DeleteItemAsync(_tableName, new Dictionary<string, AttributeValue>
            {
                ["setting_key"] = item["setting_key"]
            });
        }
    }
}
