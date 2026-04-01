using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.DynamoDBv2;
using Amazon.IoT;
using Amazon.IotData;
using Amazon.KinesisVideo;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using SnoutSpotter.Api;
using SnoutSpotter.Api.Services;

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

builder.Services.AddSingleton<IAmazonKinesisVideo, AmazonKinesisVideoClient>();

// Application services
builder.Services.AddSingleton<IStreamService, StreamService>();
builder.Services.AddSingleton<IS3UrlService, S3UrlService>();
builder.Services.AddSingleton<IClipService, ClipService>();
builder.Services.AddSingleton<IS3PresignService, S3PresignService>();
builder.Services.AddSingleton<IHealthService, HealthService>();
builder.Services.AddSingleton<IPiUpdateService, PiUpdateService>();
builder.Services.AddSingleton<ILogService, LogService>();
builder.Services.AddSingleton<ILabelService, LabelService>();
builder.Services.AddSingleton<IExportService, ExportService>();

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

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
