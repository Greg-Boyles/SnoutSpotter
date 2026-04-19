using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Lambda.Spc.Models;
using SnoutSpotter.Lambda.Spc.Services.Interfaces;

namespace SnoutSpotter.Lambda.Spc.Services;

public class UserMembershipService : IUserMembershipService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public UserMembershipService(IAmazonDynamoDB dynamoDb, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _tableName = config.Value.UsersTable;
    }

    public async Task<UserProfile?> GetByIdAsync(string userId)
    {
        var response = await _dynamoDb.GetItemAsync(_tableName,
            new Dictionary<string, AttributeValue> { ["user_id"] = new() { S = userId } });

        return response.IsItemSet ? FromItem(response.Item) : null;
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
            Households: households);
    }
}
