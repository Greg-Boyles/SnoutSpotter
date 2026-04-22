using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Logging;
using SnoutSpotter.Spc.Client.Models;
using SnoutSpotter.Spc.Client.Services.Interfaces;

namespace SnoutSpotter.Spc.Client.Services;

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
            return await UpdateExistingAsync(name, body, ct);
        }
        catch (InvalidRequestException ex) when (IsScheduledForDeletion(ex))
        {
            // The secret exists but is in the 7-day recovery window from a
            // previous unlink. Restore it, then overwrite the body.
            _log.LogInformation("Restoring SPC secret scheduled for deletion before re-link {HouseholdId}", householdId);
            await _sm.RestoreSecretAsync(new RestoreSecretRequest { SecretId = name }, ct);
            return await UpdateExistingAsync(name, body, ct);
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
        catch (InvalidRequestException ex) when (IsScheduledForDeletion(ex))
        {
            // Secret exists in the 7-day recovery window from a previous unlink.
            // To re-link callers, surface as "no current value" — SaveAsync will
            // restore-and-overwrite on the next write.
            return null;
        }
    }

    private async Task<string> UpdateExistingAsync(string name, string body, CancellationToken ct)
    {
        var updated = await _sm.PutSecretValueAsync(new PutSecretValueRequest
        {
            SecretId = name,
            SecretString = body
        }, ct);
        return updated.ARN;
    }

    private static bool IsScheduledForDeletion(InvalidRequestException ex)
        => ex.Message.Contains("marked for deletion", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("scheduled for deletion", StringComparison.OrdinalIgnoreCase);

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
