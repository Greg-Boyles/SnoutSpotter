using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using SnoutSpotter.Lambda.Spc.Models;

namespace SnoutSpotter.Lambda.Spc.Services;

public class SpcSecretsStore : ISpcSecretsStore
{
    private readonly IAmazonSecretsManager _sm;
    private readonly ILogger<SpcSecretsStore> _log;

    public SpcSecretsStore(IAmazonSecretsManager sm, ILogger<SpcSecretsStore> log)
    {
        _sm = sm;
        _log = log;
    }

    public async Task<string> SaveAsync(string householdId, SpcSecret secret, CancellationToken ct = default)
    {
        var name = SecretName(householdId);
        var body = JsonSerializer.Serialize(secret);
        try
        {
            var created = await _sm.CreateSecretAsync(new CreateSecretRequest
            {
                Name = name,
                SecretString = body,
                Description = $"Sure Pet Care access token for {householdId}",
                Tags = new List<Tag>
                {
                    new() { Key = "Project", Value = "SnoutSpotter" },
                    new() { Key = "HouseholdId", Value = householdId }
                }
            }, ct);
            return created.ARN;
        }
        catch (ResourceExistsException)
        {
            var updated = await _sm.PutSecretValueAsync(new PutSecretValueRequest
            {
                SecretId = name,
                SecretString = body
            }, ct);
            return updated.ARN;
        }
    }

    public async Task<SpcSecret?> GetAsync(string householdId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _sm.GetSecretValueAsync(new GetSecretValueRequest
            {
                SecretId = SecretName(householdId)
            }, ct);
            if (string.IsNullOrEmpty(resp.SecretString))
                return null;
            return JsonSerializer.Deserialize<SpcSecret>(resp.SecretString);
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }

    public async Task DeleteAsync(string householdId, CancellationToken ct = default)
    {
        try
        {
            // 7-day recovery window keeps accidental unlinks recoverable during QA.
            await _sm.DeleteSecretAsync(new DeleteSecretRequest
            {
                SecretId = SecretName(householdId),
                RecoveryWindowInDays = 7
            }, ct);
        }
        catch (ResourceNotFoundException)
        {
            _log.LogInformation("SPC secret already absent for household {HouseholdId}", householdId);
        }
    }

    private static string SecretName(string householdId) => $"snoutspotter/spc/{householdId}";
}
