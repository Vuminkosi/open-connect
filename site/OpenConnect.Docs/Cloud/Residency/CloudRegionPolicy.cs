namespace OpenConnect.Docs.Cloud;

public sealed class CloudRegionPolicy
{
    public Guid TenantId { get; set; }
    public required string AllowedRegions { get; set; }
    public int Revision { get; set; }
}

public sealed record CloudRegionPolicyView(IReadOnlyList<string> AllowedRegions, int Revision);
public sealed record CloudRegionPolicyResult(string Status, CloudRegionPolicyView? Policy = null);

public interface ICloudRegionPolicyStore
{
    Task<CloudRegionPolicyResult> GetAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken);
    Task<CloudRegionPolicyResult> PutAsync(CloudSubject actor, Guid tenantId, string[] regions, int revision, CancellationToken cancellationToken);
    Task RecordRejectedAsync(CloudSubject actor, Guid tenantId, string outcome, CancellationToken cancellationToken);
}

public sealed class CloudRegionPolicyService(ICloudRegionPolicyStore store)
{
    public Task<CloudRegionPolicyResult> GetAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken) =>
        tenantId == Guid.Empty ? Task.FromResult(new CloudRegionPolicyResult("not_found")) : store.GetAsync(actor, tenantId, cancellationToken);

    public Task RecordInvalidAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken) => store.RecordRejectedAsync(actor, tenantId, "invalid_request", cancellationToken);

    public async Task<CloudRegionPolicyResult> PutAsync(CloudSubject actor, Guid tenantId, string[]? regions, int revision, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty || revision < 0 || revision == int.MaxValue || regions is null || regions.Length > 32 ||
            regions.Any(x => !CloudDeploymentRegistry.ValidToken(x, 80)) || regions.Distinct(StringComparer.Ordinal).Count() != regions.Length)
        {
            await RecordInvalidAsync(actor, tenantId, cancellationToken);
            return new("invalid_request");
        }
        return await store.PutAsync(actor, tenantId, regions.Order(StringComparer.Ordinal).ToArray(), revision, cancellationToken);
    }
}
