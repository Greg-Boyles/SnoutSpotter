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
    private readonly string _labelsTable;
    private readonly string _clipsTable;

    public PetService(IAmazonDynamoDB dynamoDb, IAmazonS3 s3, IOptions<AppConfig> config)
    {
        _dynamoDb = dynamoDb;
        _s3 = s3;
        _tableName = config.Value.PetsTable;
        _bucketName = config.Value.BucketName;
        _labelsTable = config.Value.LabelsTable;
        _clipsTable = config.Value.ClipsTable;
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

    public async Task<MigrationResult> MigrateLegacyLabelsAsync(string petId)
    {
        // Verify pet exists
        if (!await ExistsAsync(petId))
            throw new InvalidOperationException($"Pet '{petId}' not found");

        var labelsUpdated = 0;
        var clipsUpdated = 0;

        // 1. Migrate labels: query by-confirmed-label GSI where confirmed_label = "my_dog"
        Dictionary<string, AttributeValue>? lastKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _labelsTable,
                IndexName = "by-confirmed-label",
                KeyConditionExpression = "confirmed_label = :label",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":label"] = new() { S = "my_dog" }
                },
                ProjectionExpression = "keyframe_key",
                ExclusiveStartKey = lastKey
            });

            // Batch update in groups of 25
            var keys = response.Items.Select(i => i["keyframe_key"].S).ToList();
            for (var i = 0; i < keys.Count; i += 25)
            {
                var batch = keys.Skip(i).Take(25).ToList();
                var writeRequests = batch.Select(k => new WriteRequest
                {
                    PutRequest = null
                }).ToList();

                // Use individual updates for labels (need to preserve other attributes)
                foreach (var key in batch)
                {
                    await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                    {
                        TableName = _labelsTable,
                        Key = new Dictionary<string, AttributeValue>
                        {
                            ["keyframe_key"] = new() { S = key }
                        },
                        UpdateExpression = "SET confirmed_label = :newLabel",
                        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                        {
                            [":newLabel"] = new() { S = petId }
                        }
                    });
                    labelsUpdated++;
                }
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey is { Count: > 0 });

        // 2. Migrate clips: query by-detection GSI where detection_type = "my_dog"
        lastKey = null;
        do
        {
            var response = await _dynamoDb.QueryAsync(new QueryRequest
            {
                TableName = _clipsTable,
                IndexName = "by-detection",
                KeyConditionExpression = "detection_type = :dt",
                ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                {
                    [":dt"] = new() { S = "my_dog" }
                },
                ProjectionExpression = "clip_id"
            });

            foreach (var item in response.Items)
            {
                var clipId = item["clip_id"].S;

                // Read full clip to update nested keyframe_detections
                var clipResponse = await _dynamoDb.GetItemAsync(_clipsTable,
                    new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } });

                if (!clipResponse.IsItemSet) continue;

                var updateExpr = "SET detection_type = :newDt";
                var exprValues = new Dictionary<string, AttributeValue>
                {
                    [":newDt"] = new() { S = petId }
                };

                // Update nested keyframe_detections if present
                if (clipResponse.Item.TryGetValue("keyframe_detections", out var kdAttr) && kdAttr.L?.Count > 0)
                {
                    var updated = false;
                    foreach (var kd in kdAttr.L)
                    {
                        if (kd.M.TryGetValue("label", out var labelAttr) && labelAttr.S == "my_dog")
                        {
                            kd.M["label"] = new AttributeValue { S = petId };
                            updated = true;
                        }

                        if (kd.M.TryGetValue("detections", out var detsAttr) && detsAttr.L?.Count > 0)
                        {
                            foreach (var det in detsAttr.L)
                            {
                                if (det.M.TryGetValue("label", out var detLabel) && detLabel.S == "my_dog")
                                {
                                    det.M["label"] = new AttributeValue { S = petId };
                                    updated = true;
                                }
                            }
                        }
                    }

                    if (updated)
                    {
                        updateExpr += ", keyframe_detections = :kd";
                        exprValues[":kd"] = kdAttr;
                    }
                }

                await _dynamoDb.UpdateItemAsync(new UpdateItemRequest
                {
                    TableName = _clipsTable,
                    Key = new Dictionary<string, AttributeValue> { ["clip_id"] = new() { S = clipId } },
                    UpdateExpression = updateExpr,
                    ExpressionAttributeValues = exprValues
                });
                clipsUpdated++;
            }

            lastKey = response.LastEvaluatedKey;
        } while (lastKey is { Count: > 0 });

        return new MigrationResult(labelsUpdated, clipsUpdated);
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
