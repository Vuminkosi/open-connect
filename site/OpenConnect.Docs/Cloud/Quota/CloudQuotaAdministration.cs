namespace OpenConnect.Docs.Cloud;

public sealed record CloudQuotaPolicy(string CapabilityId, string Unit, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, long Limit);
public sealed record CloudQuotaPolicyResult(string Status, CloudQuotaBucket? Bucket = null);

public interface ICloudQuotaAdministrationStore
{
    Task<CloudQuotaBucket?> GetAsync(Guid tenantId, Guid applicationId, Guid bucketId, CancellationToken cancellationToken);
    Task<CloudQuotaPolicyResult> PutAsync(CloudSubject actor, string traceId, Guid tenantId, Guid applicationId, Guid bucketId, CloudQuotaPolicy policy, long revision, CancellationToken cancellationToken);
    Task RecordAsync(CloudSubject actor, string traceId, Guid tenantId, Guid bucketId, string outcome, bool denied, CancellationToken cancellationToken);
}

public sealed class CloudQuotaAdministration(ICloudQuotaAdministrationStore store)
{
    public Task<CloudQuotaBucket?> GetAsync(Guid tenantId, Guid applicationId, Guid bucketId, CancellationToken cancellationToken) =>
        store.GetAsync(tenantId, applicationId, bucketId, cancellationToken);

    public Task RecordAsync(CloudSubject actor, string traceId, Guid tenantId, Guid bucketId, string outcome, bool denied, CancellationToken cancellationToken) =>
        store.RecordAsync(actor, traceId, tenantId, bucketId, outcome, denied, cancellationToken);

    public async Task<CloudQuotaPolicyResult> PutAsync(CloudSubject actor, string traceId, Guid tenantId, Guid applicationId, Guid bucketId, CloudQuotaPolicy policy, long revision, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || applicationId == Guid.Empty || bucketId == Guid.Empty || revision < 0 || revision == long.MaxValue ||
            !CloudDeploymentRegistry.ValidToken(policy.CapabilityId, 80) || !CloudDeploymentRegistry.ValidToken(policy.Unit, 80) ||
            policy.Limit < 0 || policy.PeriodStart.Offset != TimeSpan.Zero || policy.PeriodEnd.Offset != TimeSpan.Zero ||
            policy.PeriodStart.Ticks % 10 != 0 || policy.PeriodEnd.Ticks % 10 != 0 || policy.PeriodStart >= policy.PeriodEnd)
        {
            await store.RecordAsync(actor, traceId, tenantId, bucketId, "invalid_request", false, cancellationToken);
            return new("invalid_request");
        }
        return await store.PutAsync(actor, traceId, tenantId, applicationId, bucketId, policy, revision, cancellationToken);
    }
}
