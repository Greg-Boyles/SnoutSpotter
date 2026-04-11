namespace SnoutSpotter.Api.Services.Interfaces;

public interface ISettingsService
{
    Task<Dictionary<string, SettingValue>> GetAllAsync();
    Task UpdateAsync(string key, string value);
    Task ResetAllAsync();
}

public record SettingValue(string Key, string Value, string Default, string Label, string Type, double Min, double Max, string Description, string[]? Options = null);
