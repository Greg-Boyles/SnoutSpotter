using Amazon.CloudWatch;
using Amazon.DynamoDBv2;
using Amazon.S3;
using SnoutSpotter.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// AWS services
builder.Services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
builder.Services.AddSingleton<IAmazonS3, AmazonS3Client>();
builder.Services.AddSingleton<IAmazonCloudWatch, AmazonCloudWatchClient>();

// Application services
builder.Services.AddSingleton<ClipService>();
builder.Services.AddSingleton<S3PresignService>();
builder.Services.AddSingleton<HealthService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// CORS
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
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
app.MapControllers();

app.Run();
