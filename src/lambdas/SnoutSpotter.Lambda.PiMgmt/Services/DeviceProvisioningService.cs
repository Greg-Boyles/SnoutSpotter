using Amazon.IoT;
using Amazon.IoT.Model;

namespace SnoutSpotter.Lambda.PiMgmt.Services;

public class DeviceProvisioningService
{
    private readonly IAmazonIoT _iot;
    private readonly string _thingGroupName;
    private readonly string _policyName;
    private readonly string _region;

    public DeviceProvisioningService(IAmazonIoT iot, IConfiguration configuration)
    {
        _iot = iot;
        _thingGroupName = configuration["IOT_THING_GROUP"]
            ?? throw new InvalidOperationException("IOT_THING_GROUP not configured");
        _policyName = configuration["IOT_POLICY_NAME"]
            ?? throw new InvalidOperationException("IOT_POLICY_NAME not configured");
        _region = configuration["AWS_REGION"] ?? "eu-west-1";
    }

    public async Task<DeviceRegistrationResult> RegisterAsync(string thingName)
    {
        try
        {
            // 1. Create IoT Thing
            await _iot.CreateThingAsync(new CreateThingRequest
            {
                ThingName = thingName
            });

            // 2. Create keys and certificate
            var certResponse = await _iot.CreateKeysAndCertificateAsync(new CreateKeysAndCertificateRequest
            {
                SetAsActive = true
            });

            var certificateArn = certResponse.CertificateArn;
            var certificatePem = certResponse.CertificatePem;
            var privateKey = certResponse.KeyPair.PrivateKey;

            try
            {
                // 3. Attach policy to certificate
                await _iot.AttachPolicyAsync(new AttachPolicyRequest
                {
                    PolicyName = _policyName,
                    Target = certificateArn
                });

                // 4. Attach certificate to thing
                await _iot.AttachThingPrincipalAsync(new AttachThingPrincipalRequest
                {
                    ThingName = thingName,
                    Principal = certificateArn
                });

                // 5. Add thing to group
                await _iot.AddThingToThingGroupAsync(new AddThingToThingGroupRequest
                {
                    ThingGroupName = _thingGroupName,
                    ThingName = thingName
                });

                // Get IoT endpoint
                var endpointResponse = await _iot.DescribeEndpointAsync(new DescribeEndpointRequest
                {
                    EndpointType = "iot:Data-ATS"
                });

                return new DeviceRegistrationResult
                {
                    ThingName = thingName,
                    CertificatePem = certificatePem,
                    PrivateKey = privateKey,
                    CertificateArn = certificateArn,
                    IoTEndpoint = endpointResponse.EndpointAddress,
                    RootCaUrl = "https://www.amazontrust.com/repository/AmazonRootCA1.pem"
                };
            }
            catch
            {
                // Cleanup on failure
                try
                {
                    await _iot.UpdateCertificateAsync(new UpdateCertificateRequest
                    {
                        CertificateId = certificateArn.Split('/').Last(),
                        NewStatus = CertificateStatus.INACTIVE
                    });
                    await _iot.DeleteCertificateAsync(new DeleteCertificateRequest
                    {
                        CertificateId = certificateArn.Split('/').Last(),
                        ForceDelete = true
                    });
                }
                catch { /* Best effort cleanup */ }
                
                throw;
            }
        }
        catch (ResourceAlreadyExistsException)
        {
            throw new InvalidOperationException($"Thing '{thingName}' already exists");
        }
    }

    public async Task DeregisterAsync(string thingName)
    {
        // 1. List and detach all principals (certificates)
        var principalsResponse = await _iot.ListThingPrincipalsAsync(new ListThingPrincipalsRequest
        {
            ThingName = thingName
        });

        foreach (var principal in principalsResponse.Principals)
        {
            // Detach certificate from thing
            await _iot.DetachThingPrincipalAsync(new DetachThingPrincipalRequest
            {
                ThingName = thingName,
                Principal = principal
            });

            // Detach policy from certificate
            try
            {
                await _iot.DetachPolicyAsync(new DetachPolicyRequest
                {
                    PolicyName = _policyName,
                    Target = principal
                });
            }
            catch { /* Policy may not be attached */ }

            // Deactivate and delete certificate
            var certificateId = principal.Split('/').Last();
            try
            {
                await _iot.UpdateCertificateAsync(new UpdateCertificateRequest
                {
                    CertificateId = certificateId,
                    NewStatus = CertificateStatus.INACTIVE
                });

                await _iot.DeleteCertificateAsync(new DeleteCertificateRequest
                {
                    CertificateId = certificateId,
                    ForceDelete = true
                });
            }
            catch { /* Certificate may already be deleted */ }
        }

        // 2. Remove thing from group
        try
        {
            await _iot.RemoveThingFromThingGroupAsync(new RemoveThingFromThingGroupRequest
            {
                ThingGroupName = _thingGroupName,
                ThingName = thingName
            });
        }
        catch { /* Thing may not be in group */ }

        // 3. Delete thing
        await _iot.DeleteThingAsync(new DeleteThingRequest
        {
            ThingName = thingName
        });
    }

    public async Task<List<string>> ListDevicesAsync()
    {
        var response = await _iot.ListThingsInThingGroupAsync(new ListThingsInThingGroupRequest
        {
            ThingGroupName = _thingGroupName
        });
        return response.Things;
    }
}

public class DeviceRegistrationResult
{
    public required string ThingName { get; init; }
    public required string CertificatePem { get; init; }
    public required string PrivateKey { get; init; }
    public required string CertificateArn { get; init; }
    public required string IoTEndpoint { get; init; }
    public required string RootCaUrl { get; init; }
}
