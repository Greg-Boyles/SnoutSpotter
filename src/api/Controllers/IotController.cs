using System.Security.Cryptography;
using System.Text;
using Amazon.IoT;
using Amazon.IoT.Model;
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
            return Ok(new
            {
                accessKeyId = creds.AccessKeyId,
                secretAccessKey = creds.SecretAccessKey,
                sessionToken = creds.SessionToken,
                iotEndpoint = _cachedIotEndpoint,
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
        string host, string accessKey, string secretKey, string sessionToken, string region)
    {
        var now = DateTime.UtcNow;
        var datestamp = now.ToString("yyyyMMdd");
        var amzDate = now.ToString("yyyyMMddTHHmmssZ");
        var service = "iotdevicegateway";
        var credentialScope = $"{datestamp}/{region}/{service}/aws4_request";

        // Build query parameters — must be sorted by key for SigV4
        // Use custom encoding that matches AWS SigV4 requirements exactly
        var qp = new SortedDictionary<string, string>
        {
            ["X-Amz-Algorithm"] = "AWS4-HMAC-SHA256",
            ["X-Amz-Credential"] = $"{accessKey}/{credentialScope}",
            ["X-Amz-Date"] = amzDate,
            ["X-Amz-Expires"] = "86400",
            ["X-Amz-SignedHeaders"] = "host"
        };

        if (!string.IsNullOrEmpty(sessionToken))
            qp["X-Amz-Security-Token"] = sessionToken;

        var canonicalQueryString = string.Join("&",
            qp.Select(kv => $"{AwsEncode(kv.Key)}={AwsEncode(kv.Value)}"));

        var canonicalHeaders = $"host:{host}\n";
        var payloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        var canonicalRequest = string.Join("\n",
            "GET", "/mqtt", canonicalQueryString, canonicalHeaders, "host", payloadHash);

        var stringToSign = string.Join("\n",
            "AWS4-HMAC-SHA256", amzDate, credentialScope,
            Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = DeriveSigningKey(secretKey, datestamp, region, service);
        var signature = Hex(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(stringToSign)));

        return $"wss://{host}/mqtt?{canonicalQueryString}&X-Amz-Signature={signature}";
    }

    /// <summary>
    /// RFC 3986 percent-encoding as required by AWS SigV4.
    /// Uri.EscapeDataString doesn't encode all characters SigV4 requires.
    /// </summary>
    private static string AwsEncode(string value)
    {
        var encoded = new StringBuilder();
        foreach (var c in value)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '~')
            {
                encoded.Append(c);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(new[] { c }))
                {
                    encoded.Append($"%{b:X2}");
                }
            }
        }
        return encoded.ToString();
    }

    private static byte[] DeriveSigningKey(string secretKey, string datestamp, string region, string service)
    {
        var kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes($"AWS4{secretKey}"), Encoding.UTF8.GetBytes(datestamp));
        var kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region));
        var kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service));
        return HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"));
    }

    private static string Hex(byte[] data) => Convert.ToHexString(data).ToLowerInvariant();
}
