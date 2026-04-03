namespace SnoutSpotter.Lambda.PiMgmt.Services;

public class DeviceRegistrationResult
{
    public required string ThingName { get; init; }
    public required string CertificatePem { get; init; }
    public required string PrivateKey { get; init; }
    public required string CertificateArn { get; init; }
    public required string IoTEndpoint { get; init; }
    public required string CredentialProviderEndpoint { get; init; }
    public required string RootCaUrl { get; init; }
}