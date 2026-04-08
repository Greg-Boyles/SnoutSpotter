using Amazon.IoT;
using SnoutSpotter.Lambda.PiMgmt.Services;

var builder = WebApplication.CreateBuilder(args);

// AWS services
builder.Services.AddSingleton<IAmazonIoT, AmazonIoTClient>();

// Application services
builder.Services.AddSingleton<IDeviceProvisioningService, DeviceProvisioningService>();

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

app.UseSwagger();
app.UseSwaggerUI();

app.UseCors();
app.MapControllers();

app.Run();
