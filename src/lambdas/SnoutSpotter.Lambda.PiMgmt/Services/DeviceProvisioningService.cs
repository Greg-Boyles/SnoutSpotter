using Amazon.IoT;
using Amazon.IoT.Model;

namespace SnoutSpotter.Lambda.PiMgmt.Services;

public class DeviceProvisioningService
{
    private readonly IAmazonIoT _iot;
    private readonly string _piThingGroupName;
    private readonly string _piPolicyName;
    private readonly string _trainerThingGroupName;
    private readonly string _trainerPolicyName;

    private readonly string? _dataBucket;

    public DeviceProvisioningService(IAmazonIoT iot, IConfiguration configuration)
    {
        _iot = iot;
        _piThingGroupName = configuration["IOT_THING_GROUP"]
            ?? throw new InvalidOperationException("IOT_THING_GROUP not configured");
        _piPolicyName = configuration["IOT_POLICY_NAME"]
            ?? throw new InvalidOperationException("IOT_POLICY_NAME not configured");
        _trainerThingGroupName = configuration["IOT_TRAINER_THING_GROUP"] ?? "snoutspotter-trainers";
        _trainerPolicyName = configuration["IOT_TRAINER_POLICY_NAME"] ?? "snoutspotter-trainer-policy";
        _dataBucket = configuration["DATA_BUCKET"];
    }

    // ── Pi devices ──

    public Task<DeviceRegistrationResult> RegisterAsync(string thingName)
        => ProvisionThingAsync(thingName, _piThingGroupName, _piPolicyName);

    public Task DeregisterAsync(string thingName)
        => DeprovisionThingAsync(thingName, _piThingGroupName, _piPolicyName);

    public Task<List<string>> ListDevicesAsync()
        => ListThingsInGroupAsync(_piThingGroupName);

    // ── Training agents ──

    public Task<DeviceRegistrationResult> RegisterTrainerAsync(string thingName)
        => ProvisionThingAsync(thingName, _trainerThingGroupName, _trainerPolicyName);

    public Task DeregisterTrainerAsync(string thingName)
        => DeprovisionThingAsync(thingName, _trainerThingGroupName, _trainerPolicyName);

    public Task<List<string>> ListTrainersAsync()
        => ListThingsInGroupAsync(_trainerThingGroupName);

    // ── Shared provisioning logic ──

    private async Task<DeviceRegistrationResult> ProvisionThingAsync(
        string thingName, string thingGroupName, string policyName)
    {
        try
        {
            await _iot.CreateThingAsync(new CreateThingRequest { ThingName = thingName });

            var certResponse = await _iot.CreateKeysAndCertificateAsync(new CreateKeysAndCertificateRequest
            {
                SetAsActive = true
            });

            var certificateArn = certResponse.CertificateArn;
            var certificatePem = certResponse.CertificatePem;
            var privateKey = certResponse.KeyPair.PrivateKey;

            try
            {
                await _iot.AttachPolicyAsync(new AttachPolicyRequest
                {
                    PolicyName = policyName,
                    Target = certificateArn
                });

                await _iot.AttachThingPrincipalAsync(new AttachThingPrincipalRequest
                {
                    ThingName = thingName,
                    Principal = certificateArn
                });

                await _iot.AddThingToThingGroupAsync(new AddThingToThingGroupRequest
                {
                    ThingGroupName = thingGroupName,
                    ThingName = thingName
                });

                var dataEndpointTask = _iot.DescribeEndpointAsync(new DescribeEndpointRequest
                {
                    EndpointType = "iot:Data-ATS"
                });
                var credEndpointTask = _iot.DescribeEndpointAsync(new DescribeEndpointRequest
                {
                    EndpointType = "iot:CredentialProvider"
                });

                await Task.WhenAll(dataEndpointTask, credEndpointTask);

                return new DeviceRegistrationResult
                {
                    ThingName = thingName,
                    CertificatePem = certificatePem,
                    PrivateKey = privateKey,
                    CertificateArn = certificateArn,
                    IoTEndpoint = dataEndpointTask.Result.EndpointAddress,
                    CredentialProviderEndpoint = credEndpointTask.Result.EndpointAddress,
                    RootCaUrl = "https://www.amazontrust.com/repository/AmazonRootCA1.pem",
                    S3Bucket = _dataBucket
                };
            }
            catch
            {
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

    private async Task DeprovisionThingAsync(string thingName, string thingGroupName, string policyName)
    {
        var principalsResponse = await _iot.ListThingPrincipalsAsync(new ListThingPrincipalsRequest
        {
            ThingName = thingName
        });

        foreach (var principal in principalsResponse.Principals)
        {
            await _iot.DetachThingPrincipalAsync(new DetachThingPrincipalRequest
            {
                ThingName = thingName,
                Principal = principal
            });

            try
            {
                await _iot.DetachPolicyAsync(new DetachPolicyRequest
                {
                    PolicyName = policyName,
                    Target = principal
                });
            }
            catch { /* Policy may not be attached */ }

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

        try
        {
            await _iot.RemoveThingFromThingGroupAsync(new RemoveThingFromThingGroupRequest
            {
                ThingGroupName = thingGroupName,
                ThingName = thingName
            });
        }
        catch { /* Thing may not be in group */ }

        await _iot.DeleteThingAsync(new DeleteThingRequest { ThingName = thingName });
    }

    private async Task<List<string>> ListThingsInGroupAsync(string thingGroupName)
    {
        var response = await _iot.ListThingsInThingGroupAsync(new ListThingsInThingGroupRequest
        {
            ThingGroupName = thingGroupName
        });
        return response.Things;
    }
}