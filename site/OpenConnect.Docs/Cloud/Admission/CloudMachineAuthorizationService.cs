namespace OpenConnect.Docs.Cloud;

public sealed record CloudMachineAuthorizationResult(bool Authorized, string Code, CloudCredentialIdentity? Identity = null);

public interface ICloudMachineCapabilityReader
{
    Task<CloudCapabilitySnapshot?> GetCurrentAsync(CloudCredentialIdentity identity, CancellationToken cancellationToken);
}

public sealed class CloudMachineAuthorizationService(
    CloudCredentialService credentials, ICloudMachineCapabilityReader capabilities, TimeProvider timeProvider)
{
    public async Task<CloudMachineAuthorizationResult> AuthorizeAsync(string? secret, string? capabilityId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(capabilityId) || capabilityId.Length > 80)
            return new(false, "invalid_request");
        try
        {
            var identity = await credentials.ValidateAsync(secret, cancellationToken);
            if (identity is null)
                return new(false, "unauthorized");
            if (!identity.Scopes.Contains(capabilityId))
                return new(false, "forbidden");
            var snapshot = await capabilities.GetCurrentAsync(identity, cancellationToken);
            if (snapshot is null)
                return new(false, "unauthorized");
            var capability = CloudCapabilityProjection.Application(snapshot, timeProvider.GetUtcNow()).FirstOrDefault(x => x.Id == capabilityId);
            if (capability is null)
                return new(false, "capability_unavailable");
            if (capability.ReasonCode is not null)
                return new(false, capability.ReasonCode);
            return new(true, "authorized", identity);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, "capability_unavailable"); }
    }
}
