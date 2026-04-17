namespace SnoutSpotter.Api.Extensions;

public static class HttpContextExtensions
{
    public static string GetHouseholdId(this HttpContext context)
        => context.Items["HouseholdId"] as string
           ?? throw new UnauthorizedAccessException("No household_id");

    public static string GetUserId(this HttpContext context)
        => context.Items["UserId"] as string
           ?? throw new UnauthorizedAccessException("No user_id");
}
