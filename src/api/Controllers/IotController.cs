using System.Security.Cryptography;
using System.Text;
using Amazon.IoT;
using Amazon.IoT.Model;
using Amazon.Runtime;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace SnoutSpotter.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class IotController : ControllerBase
{
    private readonly IAmazonSecurityTokenService _sts;
    private readonly IAmazonIoT _iot;
    private readonly string _browserIotRoleArn;

    private static string? _cachedIotEndpoint;

    public IotController(IAmazonSecurityTokenService sts, IAmazonIoT iot, IConfiguration configuration)
    {
        _sts = sts;
        _iot = iot;
        _browserIotRoleArn = configuration["BROWSER_IOT_ROLE_ARN"]
            ?? throw new InvalidOperationException("BROWSER_IOT_ROLE_ARN not configured");
    }

    [HttpGet("credentials")]
    public async Task<ActionResult> GetIotCredentials()
    {
        try
        {
            if (_cachedIotEndpoint == null)
            {
                var endpoint = await _iot.DescribeEndpointAsync(new DescribeEndpointRequest
                {
                    EndpointType = "iot:Data-ATS"
                });
                _cachedIotEndpoint = endpoint.EndpointAddress;
            }

            var clientId = $"browser-{Guid.NewGuid():N}";

            var assumeResponse = await _sts.AssumeRoleAsync(new AssumeRoleRequest
            {
                RoleArn = _browserIotRoleArn,
                RoleSessionName = clientId,
                DurationSeconds = 3600
            });

            var creds = assumeResponse.Credentials;
            var presignedUrl = CreatePresignedMqttUrl(
                _cachedIotEndpoint,
                new ImmutableCredentials(creds.AccessKeyId, creds.SecretAccessKey, creds.SessionToken),
                "eu-west-1"
            );

            return Ok(new
            {
                presignedUrl,
                clientId,
                region = "eu-west-1",
                expiration = creds.Expiration.ToString("O")
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static string CreatePresignedMqttUrl(
        string host, ImmutableCredentials credentials, string region)
    {
        var now = DateTime.UtcNow;
        var datestamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var service = "iotdevicegateway";
        var credentialScope = $"{datestamp}/{region}/{service}/aws4_request";

        var queryParams = new SortedDictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{credentials.AccessKey}/{credentialScope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = "86400",
            ["X-Amz-SignedHeaders"] = "host"
        };

        if (!string.IsNullOrEmpty(credentials.Token))
            queryParams["X-Amz-Security-Token"] = credentials.Token;

        var canonicalQueryString = string.Join("&",
            queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

        var canonicalRequest =
            $"GET\n/mqtt\n{canonicalQueryString}\nhost:{host}\n\nhost\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var stringToSign =
            $"AWS4-HMAC-SHA256\n{amzDate}\n{credentialScope}\n" +
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = GetSignatureKey(credentials.SecretKey, datestamp, region, service);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return $"wss://{host}/mqtt?{canonicalQueryString}&X-Amz-Signature={signature}";
    }

    private static byte[] GetSignatureKey(string key, string datestamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{key}"), Encoding.UTF8.GetBytes(datestamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
