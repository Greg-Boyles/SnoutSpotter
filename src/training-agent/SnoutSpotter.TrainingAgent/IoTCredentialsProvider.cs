using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Amazon.Runtime;

namespace SnoutSpotter.TrainingAgent;

/// <summary>
/// Fetches temporary AWS credentials via the IoT Credentials Provider endpoint
/// using the device X.509 certificate — same pattern as the Pi's iot_credential_provider.py.
/// Credentials are automatically refreshed 5 minutes before expiry.
/// </summary>
public class IoTCredentialsProvider : RefreshingAWSCredentials
{
    private readonly string _url;
    private readonly string _thingName;
    private readonly X509Certificate2 _clientCert;
    private readonly ILogger _logger;

    public IoTCredentialsProvider(
        string endpoint,
        string roleAlias,
        string thingName,
        string certPath,
        string keyPath,
        ILogger logger)
    {
        _url = $"https://{endpoint}/role-aliases/{roleAlias}/credentials";
        _thingName = thingName;
        _clientCert = X509Certificate2.CreateFromPemFile(certPath, keyPath);
        _logger = logger;
        PreemptExpiryTime = TimeSpan.FromMinutes(5);
    }

    protected override CredentialsRefreshState GenerateNewCredentials()
    {
        _logger.LogInformation("Fetching IoT credentials from credentials provider...");

        var handler = new HttpClientHandler();
        handler.ClientCertificates.Add(_clientCert);

        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.Add("x-amzn-iot-thingname", _thingName);

        var response = http.GetAsync(_url).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        var creds = body.RootElement.GetProperty("credentials");

        var accessKey    = creds.GetProperty("accessKeyId").GetString()!;
        var secretKey    = creds.GetProperty("secretAccessKey").GetString()!;
        var sessionToken = creds.GetProperty("sessionToken").GetString()!;
        var expiration   = DateTime.Parse(creds.GetProperty("expiration").GetString()!).ToUniversalTime();

        _logger.LogInformation("IoT credentials obtained, expire at {Expiry:s}Z", expiration);

        return new CredentialsRefreshState(
            new ImmutableCredentials(accessKey, secretKey, sessionToken),
            expiration);
    }
}
