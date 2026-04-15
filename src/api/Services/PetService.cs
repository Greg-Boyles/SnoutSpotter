using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api.Models;
using SnoutSpotter.Api.Services.Interfaces;

namespace SnoutSpotter.Api.Services;

public partial class PetService : IPetService
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonS3 _s3;
    private readonly string _tableName;
    private readonly string _bucketName;

    public PetService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _tableName = config.Value.PetsTable;
        _bucketName = config.Value.BucketName;
    }

    public async Task<List<PetProfile>> ListAsync(string householdId = "default")
    {
        var response = await _dynamoDb.QueryAsync(new QueryRequest
        {
            TableName = _tableName,
            KeyConditionExpression = "household_id = :hid",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":hid"] = new() { S = householdId }
            }
        });

        return response.Items.Select(FromItem).OrderBy(p => p.CreatedAt).ToList();
    }

    public async Task<PetProfile?> GetAsync(string petId, string householdId = "default")
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["pet_id"] = new() { S = petId }
            }
        });

        return response.Item?.Count > 0 ? FromItem(response.Item) : null;
    }

    public async Task<PetProfile> CreateAsync(CreatePetRequest request, string householdId = "default")
    {
        var slug = SlugRegex().Replace(request.Name.ToLowerInvariant(), "");
        if (string.IsNullOrEmpty(slug)) slug = "pet";
        var rand = Guid.NewGuid().ToString("N")[..4];
        var petId = $"pet-{slug}-{rand}";

        var now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        var item = new Dictionary<string, AttributeValue>
        {
            ["household_id"] = new() { S = householdId },
            ["pet_id"] = new() { S = petId },
            ["name"] = new() { S = request.Name },
            ["created_at"] = new() { S = now }
        };

        if (!string.IsNullOrEmpty(request.Breed))
            item["breed"] = new() { S = request.Breed };

        await _dynamoDb.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(pet_id)"
        });

        return new PetProfile(householdId, petId, request.Name, request.Breed, null, now);
    }

    public async Task<PetProfile> UpdateAsync(string petId, UpdatePetRequest request, string householdId = "default")
    {
        var updateExpr = "SET #n = :name";
        var exprNames = new Dictionary<string, string> { ["#n"] = "name" };
        var exprValues = new Dictionary<string, AttributeValue>
        {
            [":name"] = new() { S = request.Name }
        };

        if (!string.IsNullOrEmpty(request.Breed))
        {
            updateExpr += ", breed = :breed";
            exprValues[":breed"] = new() { S = request.Breed };
        }
        else
        {
            updateExpr += " REMOVE breed";
        }

        var response = await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["pet_id"] = new() { S = petId }
            },
            UpdateExpression = updateExpr,
            ExpressionAttributeNames = exprNames,
            ExpressionAttributeValues = exprValues,
            ConditionExpression = "attribute_exists(pet_id)",
            ReturnValues = ReturnValue.ALL_NEW
        });

        return FromItem(response.Attributes);
    }

    public async Task DeleteAsync(string petId, string householdId = "default")
    {
        // Check if pet is referenced in the active model's class_map
        if (await IsPetInActiveClassMap(petId))
            throw new InvalidOperationException(
                $"Cannot delete pet '{petId}' — it is referenced in the active model's class_map. " +
                "Retrain the model without this pet first.");

        await _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["pet_id"] = new() { S = petId }
            },
            ConditionExpression = "attribute_exists(pet_id)"
        });
    }

    public async Task<bool> ExistsAsync(string petId, string householdId = "default")
    {
        var response = await _dynamoDb.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                ["household_id"] = new() { S = householdId },
                ["pet_id"] = new() { S = petId }
            },
            ProjectionExpression = "pet_id"
        });

        return response.Item?.Count > 0;
    }

    private async Task<bool> IsPetInActiveClassMap(string petId)
    {
        try
        {
            var response = await _s3.GetObjectAsync(new GetObjectRequest
            {
                BucketName = _bucketName,
                Key = "models/dog-classifier/class_map.json"
            });

            using var reader = new StreamReader(response.ResponseStream);
            var json = await reader.ReadToEndAsync();
            var classMap = JsonSerializer.Deserialize<ClassMap>(json);
            return classMap?.Classes?.Contains(petId) == true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private static PetProfile FromItem(Dictionary<string, AttributeValue> item) => new(
        HouseholdId: item["household_id"].S,
        PetId: item["pet_id"].S,
        Name: item["name"].S,
        Breed: item.TryGetValue("breed", out var b) ? b.S : null,
        PhotoUrl: item.TryGetValue("photo_url", out var p) ? p.S : null,
        CreatedAt: item["created_at"].S);

    private record ClassMap(string[]? Classes);

    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex SlugRegex();
}
