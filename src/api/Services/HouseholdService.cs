using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public class HouseholdService : IHouseholdService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _householdsTable;
    private readonly string _usersTable;

    public HouseholdService(IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _householdsTable = config.Value.HouseholdsTable;
        _usersTable = config.Value.UsersTable;
    }

    public async Task<HouseholdInfo> CreateAsync(string name, string ownerUserId)
    {
        var slug = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (string.IsNullOrEmpty(slug)) slug = "household";
        var rand = Guid.NewGuid().ToString()[..4];
        var householdId = $"hh-{slug}-{rand}";
        var now = DateTime.UtcNow.ToString("O");

        await _dynamoDb.PutItemAsync(_householdsTable, new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["name"] = new() { S = name },
            ["created_at"] = new() { S = now }
        });

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _usersTable,
            Key = new Dictionary<string, AttributeValue> { ["user_id"] = new() { S = ownerUserId } },
            UpdateExpression = "SET households = list_append(households, :entry)",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":entry"] = new()
                {
                    L = new List<AttributeValue>
                    {
                        new()
                        {
                            M = new Dictionary<string, AttributeValue>
                            {
                                ["householdId"] = new() { S = householdId },
                                ["role"] = new() { S = "owner" },
                                ["joinedAt"] = new() { S = now }
                            }
                        }
                    }
                }
            }
        });

        return new HouseholdInfo(householdId, name, now);
    }

    public async Task<HouseholdInfo?> GetByIdAsync(string householdId)
    {
        var response = await _dynamoDb.GetItemAsync(_householdsTable,
            new Dictionary<string, AttributeValue> { ["household_id"] = new() { S = householdId } });

        if (!response.IsItemSet) return null;
        var item = response.Item;
        return new HouseholdInfo(
            item.GetValueOrDefault("household_id")?.S ?? "",
            item.GetValueOrDefault("name")?.S ?? "",
            item.GetValueOrDefault("created_at")?.S ?? "");
    }

    public async Task<List<HouseholdInfo>> GetForUserAsync(UserProfile user)
    {
        var results = new List<HouseholdInfo>();
        foreach (var membership in user.Households)
        {
            var hh = await GetByIdAsync(membership.HouseholdId);
            if (hh != null) results.Add(hh);
        }
        return results;
    }
}
