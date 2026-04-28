using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;
using SnoutSpotter.Spc.Client.Services;
using SnoutSpotter.Spc.Client.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class DeviceRegistryService : IDeviceRegistryService
{
    private const string SourceSnoutspotter = "snoutspotter";
    private const string SourceSpc = "spc";

    private readonly IAmazonDynamoDB _dynamo;
    private readonly IPiUpdateService _piUpdate;
    private readonly IDeviceOwnershipService _ownership;
    private readonly ISpcSecretsStore _spcSecrets;
    private readonly ISpcApiClient _spcClient;
    private readonly string _tableName;

    public DeviceRegistryService(
        IAmazonDynamoDB dynamo,
        IPiUpdateService piUpdate,
        IDeviceOwnershipService ownership,
        ISpcSecretsStore spcSecrets,
        ISpcApiClient spcClient,
        IOptions<AppConfig> config)
    {
        _dynamo = dynamo;
        _piUpdate = piUpdate;
        _ownership = ownership;
        _spcSecrets = spcSecrets;
        _spcClient = spcClient;
        _tableName = config.Value.DevicesTable;
    }

    public async Task<DeviceListResponse> ListAsync(string householdId)
    {
        // Single Query returns every row for the household regardless of SK prefix.
        var resp = await _dynamo.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "household_id = :hh",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":hh"] = new() { S = householdId }
            }
        });

        var snoutspotter = new List<SnoutSpotterDeviceDto>();
        var spc = new List<SpcDeviceDto>();
        var links = new List<DeviceLinkDto>();
        var existingThings = new HashSet<string>();

        foreach (var item in resp.Items)
        {
            var sk = item["sk"].S;
            if (sk.StartsWith("snoutspotter#"))
            {
                var thing = item["thing_name"].S;
                existingThings.Add(thing);
                snoutspotter.Add(ToSnoutspotterDto(item));
            }
            else if (sk.StartsWith("spc#"))
            {
                spc.Add(ToSpcDto(item));
            }
            else if (sk.StartsWith("link#"))
            {
                links.Add(new DeviceLinkDto(
                    SpcDeviceId: item["spc_device_id"].S,
                    ThingName: item["thing_name"].S,
                    CreatedAt: item["created_at"].S));
            }
        }

        // Lazy-create snoutspotter# rows for any IoT Thing in this household that
        // doesn't yet have one. PiMgmt /register is unauthed so we can't create
        // rows there; we reconcile on first authenticated read instead.
        var things = await _piUpdate.ListPisAsync(householdId);
        foreach (var thing in things)
        {
            if (existingThings.Contains(thing)) continue;
            var created = await CreateSnoutspotterRowAsync(householdId, thing);
            snoutspotter.Add(created);
        }

        return new DeviceListResponse(
            snoutspotter.OrderBy(d => d.DisplayName).ToList(),
            spc.OrderBy(d => d.DisplayName).ToList(),
            links);
    }

    public async Task<SnoutSpotterDeviceDto> UpdateSnoutSpotterAsync(string householdId, string thingName, UpdateDeviceRequest req)
    {
        await AssertThingOwnership(householdId, thingName);

        var now = IsoNow();
        var updateExpr = "SET display_name = :dn, updated_at = :ts";
        var values = new Dictionary<string, AttributeValue>
        {
            [":dn"] = new() { S = req.DisplayName },
            [":ts"] = new() { S = now },
            [":srcInit"] = new() { S = SourceSnoutspotter },
            [":thingInit"] = new() { S = thingName },
            [":tsInit"] = new() { S = now }
        };
        // Conditional init — only writes source/thing_name/created_at when the row is new.
        updateExpr +=
            ", #src = if_not_exists(#src, :srcInit)" +
            ", thing_name = if_not_exists(thing_name, :thingInit)" +
            ", created_at = if_not_exists(created_at, :tsInit)";
        if (!string.IsNullOrEmpty(req.Notes))
        {
            updateExpr += ", notes = :notes";
            values[":notes"] = new AttributeValue { S = req.Notes };
        }
        else
        {
            updateExpr += " REMOVE notes";
        }

        var resp = await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = SnoutspotterKey(householdId, thingName),
            UpdateExpression = updateExpr,
            ExpressionAttributeNames = new Dictionary<string, string> { ["#src"] = "source" },
            ExpressionAttributeValues = values,
            ReturnValues = ReturnValue.ALL_NEW
        });

        return ToSnoutspotterDto(resp.Attributes);
    }

    public async Task<SpcDeviceDto> UpdateSpcAsync(string householdId, string spcDeviceId, UpdateDeviceRequest req)
    {
        // Upsert — lazy-create the row if it doesn't exist yet. We do NOT hit SPC
        // here; the row's spc_product_id / spc_name / serial_number start empty
        // until the user explicitly refreshes or the row was created via /link.
        var now = IsoNow();
        var updateExpr = "SET display_name = :dn, updated_at = :ts" +
                         ", #src = if_not_exists(#src, :srcInit)" +
                         ", spc_device_id = if_not_exists(spc_device_id, :idInit)" +
                         ", created_at = if_not_exists(created_at, :tsInit)";
        var values = new Dictionary<string, AttributeValue>
        {
            [":dn"] = new() { S = req.DisplayName },
            [":ts"] = new() { S = now },
            [":srcInit"] = new() { S = SourceSpc },
            [":idInit"] = new() { S = spcDeviceId },
            [":tsInit"] = new() { S = now }
        };
        if (!string.IsNullOrEmpty(req.Notes))
        {
            updateExpr += ", notes = :notes";
            values[":notes"] = new AttributeValue { S = req.Notes };
        }
        else
        {
            updateExpr += " REMOVE notes";
        }

        var resp = await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = SpcKey(householdId, spcDeviceId),
            UpdateExpression = updateExpr,
            ExpressionAttributeNames = new Dictionary<string, string> { ["#src"] = "source" },
            ExpressionAttributeValues = values,
            ReturnValues = ReturnValue.ALL_NEW
        });
        return ToSpcDto(resp.Attributes);
    }

    public async Task<SpcDeviceDto> RefreshSpcFromSpcAsync(string householdId, string spcDeviceId)
    {
        // Pull the household's SPC access token; if missing the user hasn't linked
        // their SPC account yet. Surface as an upstream error.
        var secret = await _spcSecrets.GetAsync(householdId)
            ?? throw new InvalidOperationException("SPC account not linked for this household");

        var state = await GetHouseholdIntegrationState(householdId)
            ?? throw new InvalidOperationException("SPC account not linked for this household");
        if (!long.TryParse(state.SpcHouseholdId, out var parsedSpcHh))
            throw new InvalidOperationException("SPC household id on the integration record is not numeric");

        List<Spc.Client.Models.SpcDeviceResource> devices;
        try
        {
            devices = await _spcClient.ListDevicesAsync(secret.AccessToken, parsedSpcHh);
        }
        catch (SpcUnauthorizedException)
        {
            throw new InvalidOperationException("SPC access token is no longer valid — re-link the account");
        }

        if (!long.TryParse(spcDeviceId, out var parsedId))
            throw new InvalidOperationException("SPC device id is not numeric");

        var match = devices.FirstOrDefault(d => d.Id == parsedId)
            ?? throw new InvalidOperationException($"SPC device {spcDeviceId} not found in household {state.SpcHouseholdId}");

        var now = IsoNow();
        var derivedName = match.Name ?? $"Device {match.Id}";

        // SPC-sourced metadata goes into a single nested spc_integration map —
        // matches the shape household and pet rows use. spc_device_id stays
        // flat at top level since it's the row identifier (also the SK).
        // Skip the map's serial_number entry when SPC returned null rather
        // than storing an explicit NULL attribute.
        var integrationMap = new Dictionary<string, AttributeValue>
        {
            ["spc_product_id"] = new() { N = match.ProductId.ToString() },
            ["spc_name"] = new() { S = derivedName },
            ["last_refreshed_at"] = new() { S = now },
            ["linked_at"] = new() { S = now }
        };
        if (!string.IsNullOrEmpty(match.SerialNumber))
            integrationMap["serial_number"] = new AttributeValue { S = match.SerialNumber };

        var resp = await _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = SpcKey(householdId, spcDeviceId),
            UpdateExpression =
                "SET #src = :src, spc_device_id = :id, spc_integration = :map, updated_at = :ts" +
                ", display_name = if_not_exists(display_name, :dnInit)" +
                ", created_at = if_not_exists(created_at, :ts)",
            ExpressionAttributeNames = new Dictionary<string, string> { ["#src"] = "source" },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":src"] = new() { S = SourceSpc },
                [":id"] = new() { S = spcDeviceId },
                [":map"] = new() { M = integrationMap },
                [":ts"] = new() { S = now },
                [":dnInit"] = new() { S = derivedName }
            },
            ReturnValues = ReturnValue.ALL_NEW
        });
        return ToSpcDto(resp.Attributes);
    }

    public async Task<DeviceLinkDto> LinkAsync(string householdId, string spcDeviceId, string thingName)
    {
        await AssertThingOwnership(householdId, thingName);

        // Make sure an spc# row exists so the list endpoint has something to show.
        // Lazy-create with mirrored SPC data if we can fetch it; fall back to a
        // minimal row if SPC is temporarily unavailable so linking doesn't fail.
        await EnsureSpcRowAsync(householdId, spcDeviceId);

        // Ensure a snoutspotter# row exists (normally created lazily on list).
        await CreateSnoutspotterRowIfMissingAsync(householdId, thingName);

        var now = IsoNow();
        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["sk"] = new() { S = LinkSk(spcDeviceId, thingName) },
                ["spc_device_id"] = new() { S = spcDeviceId },
                ["thing_name"] = new() { S = thingName },
                ["created_at"] = new() { S = now }
            }
        });
        return new DeviceLinkDto(spcDeviceId, thingName, now);
    }

    public async Task UnlinkAsync(string householdId, string spcDeviceId, string thingName)
    {
        await _dynamo.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["sk"] = new() { S = LinkSk(spcDeviceId, thingName) }
            }
        });
    }

    // ----- helpers -----

    private async Task AssertThingOwnership(string householdId, string thingName)
    {
        if (!await _ownership.DeviceBelongsToHouseholdAsync(thingName, householdId))
            throw new UnauthorizedAccessException($"Device {thingName} is not part of this household");
    }

    private async Task<SnoutSpotterDeviceDto> CreateSnoutspotterRowAsync(string householdId, string thingName)
    {
        var now = IsoNow();
        var item = new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["sk"] = new() { S = $"snoutspotter#{thingName}" },
            ["source"] = new() { S = SourceSnoutspotter },
            ["thing_name"] = new() { S = thingName },
            ["display_name"] = new() { S = thingName },
            ["created_at"] = new() { S = now },
            ["updated_at"] = new() { S = now }
        };
        try
        {
            await _dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = _tableName,
                Item = item,
                ConditionExpression = "attribute_not_exists(sk)"
            });
        }
        catch (ConditionalCheckFailedException)
        {
            // Race: another request created the row in between. Re-read.
            var got = await _dynamo.GetItemAsync(_tableName, new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["sk"] = new() { S = $"snoutspotter#{thingName}" }
            });
            if (got.IsItemSet) return ToSnoutspotterDto(got.Item);
        }
        return ToSnoutspotterDto(item);
    }

    private async Task CreateSnoutspotterRowIfMissingAsync(string householdId, string thingName)
    {
        var got = await _dynamo.GetItemAsync(_tableName, new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["sk"] = new() { S = $"snoutspotter#{thingName}" }
        });
        if (!got.IsItemSet)
            await CreateSnoutspotterRowAsync(householdId, thingName);
    }

    private async Task EnsureSpcRowAsync(string householdId, string spcDeviceId)
    {
        var got = await _dynamo.GetItemAsync(_tableName, new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["sk"] = new() { S = $"spc#{spcDeviceId}" }
        });
        if (got.IsItemSet) return;

        // Try to mirror fresh SPC data; if SPC is unreachable fall back to a
        // minimal row so the link can still proceed.
        try
        {
            await RefreshSpcFromSpcAsync(householdId, spcDeviceId);
            return;
        }
        catch
        {
            // Fall through to minimal row.
        }

        var now = IsoNow();
        await _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["sk"] = new() { S = $"spc#{spcDeviceId}" },
                ["source"] = new() { S = SourceSpc },
                ["spc_device_id"] = new() { S = spcDeviceId },
                ["display_name"] = new() { S = $"SPC {spcDeviceId}" },
                ["created_at"] = new() { S = now },
                ["updated_at"] = new() { S = now }
            },
            ConditionExpression = "attribute_not_exists(sk)"
        });
    }

    private async Task<SpcIntegrationStateLocal?> GetHouseholdIntegrationState(string householdId)
    {
        var resp = await _dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = "snout-spotter-households",
            Key = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId }
            }
        });
        if (!resp.IsItemSet || !resp.Item.TryGetValue("spc_integration", out var map) || map.M == null)
            return null;
        var m = map.M;
        return new SpcIntegrationStateLocal(
            SpcHouseholdId: m.GetValueOrDefault("spc_household_id")?.S ?? "",
            Status: m.GetValueOrDefault("status")?.S ?? "unknown");
    }

    private static string LinkSk(string spcDeviceId, string thingName)
        => $"link#spc#{spcDeviceId}#snoutspotter#{thingName}";

    private static Dictionary<string, AttributeValue> SnoutspotterKey(string householdId, string thingName)
        => new()
        {
            ["household_id"] = new() { S = householdId },
            ["sk"] = new() { S = $"snoutspotter#{thingName}" }
        };

    private static Dictionary<string, AttributeValue> SpcKey(string householdId, string spcDeviceId)
        => new()
        {
            ["household_id"] = new() { S = householdId },
            ["sk"] = new() { S = $"spc#{spcDeviceId}" }
        };

    private static string IsoNow() => DateTime.UtcNow.ToString("O");

    private static SnoutSpotterDeviceDto ToSnoutspotterDto(Dictionary<string, AttributeValue> item)
        => new(
            ThingName: item.GetValueOrDefault("thing_name")?.S ?? "",
            DisplayName: item.GetValueOrDefault("display_name")?.S ?? "",
            Notes: item.GetValueOrDefault("notes")?.S,
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "",
            UpdatedAt: item.GetValueOrDefault("updated_at")?.S ?? "");

    private static SpcDeviceDto ToSpcDto(Dictionary<string, AttributeValue> item)
    {
        // SPC-sourced metadata (product_id, name, serial, last_refreshed_at)
        // lives inside a nested spc_integration map — same pattern as
        // households and pets. spc_device_id stays flat because it's the
        // row's key identifier (also in SK). Flatten back out for the DTO
        // so the HTTP contract doesn't change.
        int? productId = null;
        string? spcName = null;
        string? serialNumber = null;
        string? lastRefreshedAt = null;
        if (item.TryGetValue("spc_integration", out var integration) && integration.M != null)
        {
            var m = integration.M;
            if (m.TryGetValue("spc_product_id", out var pid) && pid.N != null && int.TryParse(pid.N, out var parsed))
                productId = parsed;
            if (m.TryGetValue("spc_name", out var sname)) spcName = sname.S;
            if (m.TryGetValue("serial_number", out var serial)) serialNumber = serial.S;
            if (m.TryGetValue("last_refreshed_at", out var lra)) lastRefreshedAt = lra.S;
        }

        return new SpcDeviceDto(
            SpcDeviceId: item.GetValueOrDefault("spc_device_id")?.S ?? "",
            SpcProductId: productId,
            SpcName: spcName,
            SerialNumber: serialNumber,
            DisplayName: item.GetValueOrDefault("display_name")?.S ?? "",
            Notes: item.GetValueOrDefault("notes")?.S,
            LastRefreshedAt: lastRefreshedAt,
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "",
            UpdatedAt: item.GetValueOrDefault("updated_at")?.S ?? "");
    }

    private record SpcIntegrationStateLocal(string SpcHouseholdId, string Status);
}
