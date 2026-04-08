namespace SnoutSpotter.TrainingAgent.Models;

public class CredentialsProviderConfig
{
    public string Endpoint { get; set; } = "";
    public string RoleAlias { get; set; } = "snoutspotter-trainer-role-alias";
}