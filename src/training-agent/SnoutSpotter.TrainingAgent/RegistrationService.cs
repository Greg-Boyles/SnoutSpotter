using System.Net.Http.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SnoutSpotter.TrainingAgent;

public static class RegistrationService
{
    private const string DefaultRegistrationUrl = "https://dl8b12wn88.execute-api.eu-west-1.amazonaws.com";

    public static async Task RegisterAsync(
        string agentName,
        string? registrationUrl,
        string certsDir,
        string configPath)
    {
        registrationUrl ??= DefaultRegistrationUrl;
        Directory.CreateDirectory(certsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        using var http = new HttpClient();

        // 1. Call registration API
        var response = await http.PostAsJsonAsync(
            $"{registrationUrl.TrimEnd('/')}/api/trainers/register",
            new { name = agentName });

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Registration failed ({(int)response.StatusCode} {response.StatusCode}): {body}");
        }

        var result = await response.Content.ReadFromJsonAsync<RegistrationResult>()
            ?? throw new InvalidOperationException("Empty response from registration API");

        // 2. Save certificate and private key
        var certPath = Path.Combine(certsDir, "certificate.pem.crt");
        var keyPath  = Path.Combine(certsDir, "private.pem.key");
        var caPath   = Path.Combine(certsDir, "AmazonRootCA1.pem");

        await File.WriteAllTextAsync(certPath, result.CertificatePem);
        await File.WriteAllTextAsync(keyPath,  result.PrivateKey);

        // 3. Download Amazon Root CA
        var rootCa = await http.GetStringAsync(result.RootCaUrl);
        await File.WriteAllTextAsync(caPath, rootCa);

        // 4. Write config.yaml (bucket left empty — discovered at runtime via ListBuckets)
        var config = new GeneratedConfig
        {
            AgentName = result.ThingName,
            Iot = new GeneratedIoTConfig
            {
                Endpoint   = result.IoTEndpoint,
                ThingName  = result.ThingName,
                CertPath   = certPath,
                KeyPath    = keyPath,
                RootCaPath = caPath
            },
            S3 = new GeneratedS3Config { Region = "eu-west-1" },
            CredentialsProvider = new GeneratedCredentialsProviderConfig
            {
                Endpoint = result.CredentialProviderEndpoint
            }
        };

        var yaml = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build()
            .Serialize(config);

        await File.WriteAllTextAsync(configPath, yaml);
    }

    private record RegistrationResult(
        [property: JsonPropertyName("thingName")]                  string ThingName,
        [property: JsonPropertyName("certificatePem")]             string CertificatePem,
        [property: JsonPropertyName("privateKey")]                 string PrivateKey,
        [property: JsonPropertyName("certificateArn")]             string CertificateArn,
        [property: JsonPropertyName("ioTEndpoint")]                string IoTEndpoint,
        [property: JsonPropertyName("credentialProviderEndpoint")] string CredentialProviderEndpoint,
        [property: JsonPropertyName("rootCaUrl")]                  string RootCaUrl);

    // Local classes mirroring AgentConfig for YAML serialisation
    private class GeneratedConfig
    {
        public string AgentName { get; set; } = "";
        public GeneratedIoTConfig Iot { get; set; } = new();
        public GeneratedS3Config S3 { get; set; } = new();
        public GeneratedCredentialsProviderConfig CredentialsProvider { get; set; } = new();
    }

    private class GeneratedIoTConfig
    {
        public string Endpoint   { get; set; } = "";
        public string ThingName  { get; set; } = "";
        public string CertPath   { get; set; } = "";
        public string KeyPath    { get; set; } = "";
        public string RootCaPath { get; set; } = "";
    }

    private class GeneratedS3Config
    {
        public string Region { get; set; } = "eu-west-1";
    }

    private class GeneratedCredentialsProviderConfig
    {
        public string Endpoint { get; set; } = "";
        public string RoleAlias { get; set; } = "snoutspotter-trainer-role-alias";
    }
}
