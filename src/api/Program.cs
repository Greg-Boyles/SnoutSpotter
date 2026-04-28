using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.DynamoDBv2;
using Amazon.ECR;
using Amazon.IoT;
using Amazon.IotData;
using Amazon.KinesisVideo;
using Amazon.Lambda;
using Amazon.S3;
using Amazon.SecretsManager;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api;
using SnoutSpotter.Api.Services;
using SnoutSpotter.Api.Services.Interfaces;
using SnoutSpotter.Api.Extensions;
using SnoutSpotter.Spc.Client.Services;
using SnoutSpotter.Spc.Client.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

// Bind AppConfig from environment variables
builder.Services.Configure<AppConfig>(cfg =>
{
    cfg.BucketName = Environment.GetEnvironmentVariable("BUCKET_NAME") ?? "";
    cfg.ClipsTable = Environment.GetEnvironmentVariable("TABLE_NAME") ?? "snout-spotter-clips";
    cfg.CommandsTable = Environment.GetEnvironmentVariable("COMMANDS_TABLE") ?? "snout-spotter-commands";
    cfg.LabelsTable = Environment.GetEnvironmentVariable("LABELS_TABLE") ?? "snout-spotter-labels";
    cfg.ExportsTable = Environment.GetEnvironmentVariable("EXPORTS_TABLE") ?? "snout-spotter-exports";
    cfg.IoTThingGroup = Environment.GetEnvironmentVariable("IOT_THING_GROUP") ?? "snoutspotter-pis";
    cfg.PiLogGroup = Environment.GetEnvironmentVariable("PI_LOG_GROUP") ?? "/snoutspotter/pi-logs";
    cfg.AutoLabelFunction = Environment.GetEnvironmentVariable("AUTO_LABEL_FUNCTION") ?? "snout-spotter-auto-label";
    cfg.ExportDatasetFunction = Environment.GetEnvironmentVariable("EXPORT_DATASET_FUNCTION") ?? "snout-spotter-export-dataset";
    cfg.InferenceFunction = Environment.GetEnvironmentVariable("INFERENCE_FUNCTION") ?? "snout-spotter-run-inference";
    cfg.TrainingJobsTable = Environment.GetEnvironmentVariable("TRAINING_JOBS_TABLE") ?? "snout-spotter-training-jobs";
    cfg.TrainerThingGroup = Environment.GetEnvironmentVariable("TRAINER_THING_GROUP") ?? "snoutspotter-trainers";
    cfg.TrainingJobQueueUrl = Environment.GetEnvironmentVariable("TRAINING_JOB_QUEUE_URL") ?? "";
    cfg.SettingsTable = Environment.GetEnvironmentVariable("SETTINGS_TABLE") ?? "snout-spotter-settings";
    cfg.BackfillQueueUrl = Environment.GetEnvironmentVariable("BACKFILL_QUEUE_URL") ?? "";
    cfg.RerunInferenceQueueUrl = Environment.GetEnvironmentVariable("RERUN_INFERENCE_QUEUE_URL") ?? "";
    cfg.ModelsTable = Environment.GetEnvironmentVariable("MODELS_TABLE") ?? "snout-spotter-models";
    cfg.StatsTable = Environment.GetEnvironmentVariable("STATS_TABLE") ?? "snout-spotter-stats";
    cfg.StatsRefreshFunctionName = Environment.GetEnvironmentVariable("STATS_REFRESH_FUNCTION") ?? "snout-spotter-stats-refresh";
    cfg.PetsTable = Environment.GetEnvironmentVariable("PETS_TABLE") ?? "snout-spotter-pets";
    cfg.UsersTable = Environment.GetEnvironmentVariable("USERS_TABLE") ?? "snout-spotter-users";
    cfg.HouseholdsTable = Environment.GetEnvironmentVariable("HOUSEHOLDS_TABLE") ?? "snout-spotter-households";
    cfg.DevicesTable = Environment.GetEnvironmentVariable("DEVICES_TABLE") ?? "snout-spotter-devices";
    cfg.SpcEventsTable = Environment.GetEnvironmentVariable("SPC_EVENTS_TABLE") ?? "snout-spotter-spc-events";
    cfg.SpcBaseUrl = Environment.GetEnvironmentVariable("SPC_BASE_URL") ?? "https://app-api.beta.surehub.io";
    cfg.OktaIssuer = Environment.GetEnvironmentVariable("OKTA_ISSUER") ?? "";
    cfg.AllowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN") ?? "";
});

// AWS services
builder.Services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
builder.Services.AddSingleton<IAmazonS3, AmazonS3Client>();
builder.Services.AddSingleton<IAmazonCloudWatch, AmazonCloudWatchClient>();
builder.Services.AddSingleton<IAmazonCloudWatchLogs, AmazonCloudWatchLogsClient>();
builder.Services.AddSingleton<IAmazonIoT>(_ =>
    new AmazonIoTClient(Amazon.RegionEndpoint.EUWest1));
builder.Services.AddSingleton<IAmazonIotData>(sp =>
{
    var iot = sp.GetRequiredService<IAmazonIoT>();
    var endpoint = iot.DescribeEndpointAsync(new Amazon.IoT.Model.DescribeEndpointRequest
    {
        EndpointType = "iot:Data-ATS"
    }).GetAwaiter().GetResult();
    return new AmazonIotDataClient(new AmazonIotDataConfig
    {
        ServiceURL = $"https://{endpoint.EndpointAddress}"
    });
});

builder.Services.AddSingleton<IAmazonECR, AmazonECRClient>();
builder.Services.AddSingleton<IAmazonKinesisVideo, AmazonKinesisVideoClient>();
builder.Services.AddSingleton<IAmazonLambda, AmazonLambdaClient>();
builder.Services.AddSingleton<IAmazonSecretsManager, AmazonSecretsManagerClient>();

// Application services
builder.Services.AddSingleton<IStreamService, StreamService>();
builder.Services.AddSingleton<IS3UrlService, S3UrlService>();
builder.Services.AddSingleton<IClipService, ClipService>();
builder.Services.AddSingleton<IS3PresignService, S3PresignService>();
builder.Services.AddSingleton<IHealthService, HealthService>();
builder.Services.AddSingleton<IDeviceOwnershipService, DeviceOwnershipService>();
builder.Services.AddSingleton<IPiUpdateService, PiUpdateService>();
builder.Services.AddSingleton<ILogService, LogService>();
builder.Services.AddSingleton<ILabelService, LabelService>();
builder.Services.AddSingleton<IExportService, ExportService>();
builder.Services.AddSingleton<ITrainingService, TrainingService>();
builder.Services.AddSingleton<ISettingsService, SettingsService>();
builder.Services.AddSingleton<IModelService, ModelService>();
builder.Services.AddSingleton<IPetService, PetService>();
builder.Services.AddSingleton<IUserService, UserService>();
builder.Services.AddSingleton<IHouseholdService, HouseholdService>();
builder.Services.AddSingleton<ISpcSecretsStore, SpcSecretsStore>();
builder.Services.AddSingleton<IDeviceRegistryService, DeviceRegistryService>();
builder.Services.AddSingleton<ISpcEventsService, SpcEventsService>();

// Typed HttpClient for Sure Pet Care. Used by the device-registry refresh path.
// Matches the SPC Lambda's Polly config: 3x retry, circuit breaker, 10s timeout;
// 401/403 are thrown as SpcUnauthorizedException and are not retried.
builder.Services.AddHttpClient<ISpcApiClient, SpcApiClient>((sp, http) =>
{
    var cfg = sp.GetRequiredService<IOptions<AppConfig>>().Value;
    http.BaseAddress = new Uri(cfg.SpcBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(10);
    http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
});
builder.Services.AddSingleton<IStatsRefreshService>(sp => new StatsRefreshService(
    sp.GetRequiredService<IAmazonDynamoDB>(),
    sp.GetRequiredService<IAmazonLambda>(),
    sp.GetRequiredService<IOptions<AppConfig>>()));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var cfg = builder.Configuration;
        options.Authority = Environment.GetEnvironmentVariable("OKTA_ISSUER");
        options.Audience = "api://default";
    });
builder.Services.AddAuthorization();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN");
        if (!string.IsNullOrEmpty(allowedOrigin))
            policy.WithOrigins(allowedOrigin);
        else
            policy.AllowAnyOrigin();
        policy.AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated != true)
    {
        await next();
        return;
    }

    var path = context.Request.Path.Value ?? "";
    if (path.StartsWith("/api/households", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        var userId = context.User.FindFirst("sub")?.Value
                     ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (userId != null)
        {
            context.Items["UserId"] = userId;
        }
        await next();
        return;
    }

    {
        var userId = context.User.FindFirst("sub")?.Value
                     ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            context.Response.StatusCode = 401;
            return;
        }

        var userService = context.RequestServices.GetRequiredService<IUserService>();
        var user = await userService.GetOrCreateAsync(userId, context.User);
        var householdId = context.Request.Headers["X-Household-Id"].FirstOrDefault();

        if (string.IsNullOrEmpty(householdId) && user.Households.Count == 1)
            householdId = user.Households[0].HouseholdId;

        // Validate household membership if a header was provided or auto-selected
        if (!string.IsNullOrEmpty(householdId))
        {
            if (!user.Households.Any(h => h.HouseholdId == householdId))
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"You are not a member of this household.\"}");
                return;
            }
            context.Items["HouseholdId"] = householdId;
        }

        // Pass through without HouseholdId if user has no households yet —
        // allows the app to work before household scoping is enforced in later phases
        context.Items["UserId"] = userId;
    }

    await next();
});

app.MapControllers();

app.Run();
