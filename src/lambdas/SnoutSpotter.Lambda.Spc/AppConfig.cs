namespace SnoutSpotter.Lambda.Spc;

public class AppConfig
{
    public string HouseholdsTable { get; set; } = "snout-spotter-households";
    public string PetsTable { get; set; } = "snout-spotter-pets";
    public string UsersTable { get; set; } = "snout-spotter-users";
    public string OktaIssuer { get; set; } = "";
    public string AllowedOrigin { get; set; } = "";
    public string SpcBaseUrl { get; set; } = "https://app-api.beta.surehub.io";
}
