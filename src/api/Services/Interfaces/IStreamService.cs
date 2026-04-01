namespace SnoutSpotter.Api.Services;

public interface IStreamService
{
    Task<StreamStartResult> StartStreamAsync(string thingName);
    Task<string?> GetHlsUrlAsync(string thingName);
    Task StopStreamAsync(string thingName);
}
