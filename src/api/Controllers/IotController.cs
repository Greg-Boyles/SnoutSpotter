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
            // Get IoT endpoint (cached)
            if (_cachedIotEndpoint == null)
            {
                var endpoint = await _iot.DescribeEndpointAsync(new DescribeEndpointRequest
                {
                    EndpointType = "iot:Data-ATS"
                });
                _cachedIotEndpoint = endpoint.EndpointAddress;
            }

            // Assume the browser IoT role for temporary credentials
            var assumeResponse = await _sts.AssumeRoleAsync(new AssumeRoleRequest
            {
                RoleArn = _browserIotRoleArn,
                RoleSessionName = $"browser-{Guid.NewGuid():N}",
                DurationSeconds = 3600
            });

            var creds = assumeResponse.Credentials;

            return Ok(new
            {
                accessKeyId = creds.AccessKeyId,
                secretAccessKey = creds.SecretAccessKey,
                sessionToken = creds.SessionToken,
                expiration = creds.Expiration.ToString("O"),
                iotEndpoint = _cachedIotEndpoint,
                region = "eu-west-1"
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
