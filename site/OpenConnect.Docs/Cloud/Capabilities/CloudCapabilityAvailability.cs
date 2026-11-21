namespace OpenConnect.Docs.Cloud;

public sealed record CloudCapabilityAvailabilityView(string CapabilityId, string Availability, long Revision);
public sealed record CloudCapabilityAvailabilityResult(string Status, CloudCapabilityAvailabilityView? Availability = null);

public interface ICloudCapabilityAvailabilityStore
{
    Task<CloudCapabilityAvailabilityView?> GetAsync(string capabilityId, CancellationToken cancellationToken);
    Task<CloudCapabilityAvailabilityResult> PutAsync(CloudSubject actor, string traceId, string capabilityId,
        string availability, long revision, CancellationToken cancellationToken);
    Task RecordAsync(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken);
}

public sealed class CloudCapabilityAvailabilityService(ICloudCapabilityAvailabilityStore store)
{
    public Task<CloudCapabilityAvailabilityView?> GetAsync(string capabilityId, CancellationToken cancellationToken) =>
        CloudDeploymentRegistry.ValidToken(capabilityId, 80) ? store.GetAsync(capabilityId, cancellationToken) : Task.FromResult<CloudCapabilityAvailabilityView?>(null);

    public Task RecordAsync(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken) =>
        store.RecordAsync(actor, traceId, capabilityId, outcome, denied, cancellationToken);

    public async Task<CloudCapabilityAvailabilityResult> PutAsync(CloudSubject actor, string traceId, string capabilityId,
        string? availability, long revision, CancellationToken cancellationToken)
    {
        if (!CloudDeploymentRegistry.ValidToken(capabilityId, 80) || availability is not ("available" or "not_configured") ||
            revision < 1 || revision == long.MaxValue)
        {
            await store.RecordAsync(actor, traceId, capabilityId, "invalid_request", false, cancellationToken);
            return new("invalid_request");
        }
        return await store.PutAsync(actor, traceId, capabilityId, availability, revision, cancellationToken);
    }
}
