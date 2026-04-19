using Amazon.DynamoDBv2;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using SnoutSpotter.Lambda.Spc;
using SnoutSpotter.Lambda.Spc.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppConfig>(cfg =>
{
    cfg.HouseholdsTable = Environment.GetEnvironmentVariable("HOUSEHOLDS_TABLE") ?? "snout-spotter-households";
    cfg.PetsTable = Environment.GetEnvironmentVariable("PETS_TABLE") ?? "snout-spotter-pets";
    cfg.UsersTable = Environment.GetEnvironmentVariable("USERS_TABLE") ?? "snout-spotter-users";
    cfg.OktaIssuer = Environment.GetEnvironmentVariable("OKTA_ISSUER") ?? "";
    cfg.AllowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN") ?? "";
    cfg.SpcBaseUrl = Environment.GetEnvironmentVariable("SPC_BASE_URL") ?? "https://app-api.beta.surehub.io";
});

builder.Services.AddSingleton<IAmazonDynamoDB, AmazonDynamoDBClient>();
builder.Services.AddSingleton<IUserMembershipService, UserMembershipService>();
builder.Services.AddMemoryCache();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = Environment.GetEnvironmentVariable("OKTA_ISSUER");
        options.Audience = "api://default";
    });
builder.Services.AddAuthorization();

builder.Services.AddControllers();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        var allowedOrigin = Environment.GetEnvironmentVariable("ALLOWED_ORIGIN");
        if (!string.IsNullOrEmpty(allowedOrigin))
            policy.WithOrigins(allowedOrigin);
        else
            policy.AllowAnyOrigin();
        policy.AllowAnyMethod().AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

// Household membership middleware — identical contract to the main API:
// validates that the authenticated Okta user is a member of the household
// named in the X-Household-Id header, and stashes UserId/HouseholdId in
// HttpContext.Items for controllers to read via HttpContextExtensions.
app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated != true)
    {
        await next();
        return;
    }

    var userId = context.User.FindFirst("sub")?.Value
                 ?? context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (string.IsNullOrEmpty(userId))
    {
        context.Response.StatusCode = 401;
        return;
    }

    var userService = context.RequestServices.GetRequiredService<IUserMembershipService>();
    var user = await userService.GetByIdAsync(userId);
    var householdId = context.Request.Headers["X-Household-Id"].FirstOrDefault();

    if (string.IsNullOrEmpty(householdId) && user?.Households.Count == 1)
        householdId = user.Households[0].HouseholdId;

    if (string.IsNullOrEmpty(householdId))
    {
        context.Response.StatusCode = 400;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"X-Household-Id header required\"}");
        return;
    }

    if (user == null || !user.Households.Any(h => h.HouseholdId == householdId))
    {
        context.Response.StatusCode = 403;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"error\":\"You are not a member of this household.\"}");
        return;
    }

    context.Items["UserId"] = userId;
    context.Items["HouseholdId"] = householdId;

    await next();
});

app.MapControllers();

app.Run();
