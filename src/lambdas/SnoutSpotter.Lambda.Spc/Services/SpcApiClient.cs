using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using SnoutSpotter.Lambda.Spc.Models;
using SnoutSpotter.Lambda.Spc.Services.Interfaces;

namespace SnoutSpotter.Lambda.Spc.Services;

public class SpcApiClient : ISpcApiClient
{
    private readonly HttpClient _http;
    private readonly ILogger<SpcApiClient> _log;

    public SpcApiClient(HttpClient http, ILogger<SpcApiClient> log)
    {
        _http = http;
        _log = log;
    }

    public async Task<SpcLoginData> LoginAsync(string email, string password, string clientUid, CancellationToken ct = default)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new SpcLoginRequest(clientUid, email, password))
        };
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized ||
            resp.StatusCode == HttpStatusCode.Forbidden ||
            resp.StatusCode == HttpStatusCode.NotFound ||
            resp.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            throw new SpcUnauthorizedException($"SPC login rejected ({(int)resp.StatusCode})");
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp, ct);
            throw new SpcUpstreamException($"SPC login failed ({(int)resp.StatusCode}): {body}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<SpcLoginResponse>(cancellationToken: ct);
        if (parsed?.Data?.Token == null || parsed.Data.User == null)
            throw new SpcUpstreamException("SPC login response missing token or user");
        return parsed.Data;
    }

    public Task<List<SpcHouseholdResource>> ListHouseholdsAsync(string accessToken, CancellationToken ct = default)
        => GetPaginatedAsync<SpcHouseholdResource>(accessToken, "/api/household", ct);

    public Task<List<SpcPetResource>> ListPetsAsync(string accessToken, long spcHouseholdId, CancellationToken ct = default)
        => GetPaginatedAsync<SpcPetResource>(accessToken, $"/api/household/{spcHouseholdId}/pet", ct);

    public Task<List<SpcDeviceResource>> ListDevicesAsync(string accessToken, long spcHouseholdId, CancellationToken ct = default)
        => GetPaginatedAsync<SpcDeviceResource>(accessToken, $"/api/household/{spcHouseholdId}/device", ct);

    private async Task<List<T>> GetPaginatedAsync<T>(string accessToken, string path, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized ||
            resp.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SpcUnauthorizedException($"SPC rejected token on {path} ({(int)resp.StatusCode})");
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await SafeReadAsync(resp, ct);
            throw new SpcUpstreamException($"SPC GET {path} failed ({(int)resp.StatusCode}): {body}");
        }

        var parsed = await resp.Content.ReadFromJsonAsync<SpcPaginated<T>>(cancellationToken: ct);
        return parsed?.Data ?? new List<T>();
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try { return await resp.Content.ReadAsStringAsync(ct); }
        catch { return "<no body>"; }
    }
}
