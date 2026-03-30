using Amazon.CloudWatch;
using Amazon.CloudWatchLogs;
using Amazon.DynamoDBv2;
using Amazon.IoT;
using Amazon.IotData;
using Amazon.KinesisVideo;
using Amazon.S3;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SnoutSpotter.Api.Services;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddSingleton<StreamService>();
builder.Services.AddSingleton<S3UrlService>();
builder.Services.AddSingleton<ClipService>();
builder.Services.AddSingleton<S3PresignService>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<PiUpdateService>();
builder.Services.AddSingleton<LogService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
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
