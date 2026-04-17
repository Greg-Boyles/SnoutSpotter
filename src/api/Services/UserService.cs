using System.Collections.Concurrent;
using System.Security.Claims;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;

namespace SnoutSpotter.Api.Services;

public class UserService : Interfaces.IUserService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (UserProfile User, DateTime Expiry)> Cache = new();

    public UserService(IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _tableName = config.Value.UsersTable;
    }

    public async Task<UserProfile> GetOrCreateAsync(string userId, ClaimsPrincipal? claims)
    {
        if (Cache.TryGetValue(userId, out var cached) && DateTime.UtcNow < cached.Expiry)
            return cached.User;

        var response = await _dynamoDb.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["user_id"] = new() { S = userId } });

        UserProfile user;
        if (response.IsItemSet)
        {
            user = FromItem(response.Item);
            await UpdateLastLoginAsync(userId, user.LastLoginAt);
        }
        else
        {
            var email = claims?.FindFirst("email")?.Value
                        ?? claims?.FindFirst(ClaimTypes.Email)?.Value;
            var name = claims?.FindFirst("name")?.Value
                       ?? claims?.FindFirst(ClaimTypes.Name)?.Value;
            var now = DateTime.UtcNow.ToString("O");

            var item = new Dictionary<string, AttributeValue>
            {
                ["user_id"] = new() { S = userId },
                ["households"] = new() { L = new List<AttributeValue>() },
                ["created_at"] = new() { S = now },
                ["last_login_at"] = new() { S = now }
            };
            if (!string.IsNullOrEmpty(email)) item["email"] = new() { S = email };
            if (!string.IsNullOrEmpty(name)) item["name"] = new() { S = name };

            await _dynamoDb.PutItemAsync(_tableName, item);
            user = FromItem(item);
        }

        Cache[userId] = (user, DateTime.UtcNow.Add(CacheTtl));
        return user;
    }

    public async Task<UserProfile?> GetByIdAsync(string userId)
    {
        var response = await _dynamoDb.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["user_id"] = new() { S = userId } });

        return response.IsItemSet ? FromItem(response.Item) : null;
    }

    public void InvalidateCache(string userId) => Cache.TryRemove(userId, out _);

    private async Task UpdateLastLoginAsync(string userId, string? lastLoginAt)
    {
        if (lastLoginAt != null &&
            DateTime.TryParse(lastLoginAt, out var last) &&
            (DateTime.UtcNow - last).TotalHours < 1)
            return;

        await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["user_id"] = new() { S = userId } },
            UpdateExpression = "SET last_login_at = :t",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":t"] = new() { S = DateTime.UtcNow.ToString("O") }
            }
        });
    }

    private static UserProfile FromItem(Dictionary<string, AttributeValue> item)
    {
        var households = new List<HouseholdMembership>();
        if (item.TryGetValue("households", out var hhAttr) && hhAttr.L != null)
        {
            foreach (var entry in hhAttr.L)
            {
                if (entry.M == null) continue;
                households.Add(new HouseholdMembership(
                    HouseholdId: entry.M.GetValueOrDefault("householdId")?.S ?? "",
                    Role: entry.M.GetValueOrDefault("role")?.S ?? "member",
                    JoinedAt: entry.M.GetValueOrDefault("joinedAt")?.S ?? ""));
            }
        }

        return new UserProfile(
            UserId: item.GetValueOrDefault("user_id")?.S ?? "",
            Email: item.GetValueOrDefault("email")?.S,
            Name: item.GetValueOrDefault("name")?.S,
            Households: households,
            CreatedAt: item.GetValueOrDefault("created_at")?.S ?? "",
            LastLoginAt: item.GetValueOrDefault("last_login_at")?.S);
    }
}
