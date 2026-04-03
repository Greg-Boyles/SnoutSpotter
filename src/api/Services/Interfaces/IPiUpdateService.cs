using System.Text.Json;

namespace SnoutSpotter.Api.Services.Interfaces;

public interface IPiUpdateService
{
    Task<List<string>> ListPisAsync();
    Task<PiShadowState?> GetPiShadowAsync(string thingName);
    Task<string?> GetRawShadowAsync(string thingName);
    Task<string?> GetLatestVersionAsync();
    Task<Dictionary<string, string>> UpdateConfigAsync(string thingName, Dictionary<string, JsonElement> changes);
    Task TriggerUpdateAsync(string thingName, string? version = null);
    Task TriggerUpdateAllAsync(string? version = null);
    Task<string> SendCommandAsync(string thingName, string action);
    Task<Dictionary<string, string>?> GetCommandFromLedgerAsync(string commandId);
    Task<List<Dictionary<string, string>>> GetCommandHistoryAsync(string thingName, int limit = 50);
}
