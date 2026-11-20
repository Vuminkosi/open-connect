namespace OpenConnect.Docs.Cloud;

public sealed class CloudCatalogRegionRevision
{
    public required string CapabilityId { get; set; }
    public long Revision { get; set; }
}

public sealed record CloudCatalogRegionsView(string CapabilityId, IReadOnlyList<string> AllowedRegions, bool Verified, long Revision);
public sealed record CloudCatalogRegionsResult(string Status, CloudCatalogRegionsView? Regions = null);

public interface ICloudCatalogRegionsStore
{
    Task<CloudCatalogRegionsView?> GetAsync(string capabilityId, CancellationToken cancellationToken);
    Task<CloudCatalogRegionsResult> PutAsync(CloudSubject actor, string traceId, string capabilityId, string[] regions, bool verified, long revision, CancellationToken cancellationToken);
    Task RecordAsync(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken);
}

public sealed class CloudCatalogRegionsService(ICloudCatalogRegionsStore store)
{
    public Task<CloudCatalogRegionsView?> GetAsync(string capabilityId, CancellationToken cancellationToken) =>
        CloudDeploymentRegistry.ValidToken(capabilityId, 80) ? store.GetAsync(capabilityId, cancellationToken) : Task.FromResult<CloudCatalogRegionsView?>(null);

    public Task RecordAsync(CloudSubject actor, string traceId, string capabilityId, string outcome, bool denied, CancellationToken cancellationToken) =>
        store.RecordAsync(actor, traceId, capabilityId, outcome, denied, cancellationToken);

    public async Task<CloudCatalogRegionsResult> PutAsync(CloudSubject actor, string traceId, string capabilityId, string[]? regions, bool verified, long revision, CancellationToken cancellationToken)
    {
        if (!CloudDeploymentRegistry.ValidToken(capabilityId, 80) || regions is null || regions.Length > 32 || revision < 1 || revision == long.MaxValue ||
            regions.Any(x => !CloudDeploymentRegistry.ValidToken(x, 80)) || regions.Distinct(StringComparer.Ordinal).Count() != regions.Length)
        {
            await store.RecordAsync(actor, traceId, capabilityId, "invalid_request", false, cancellationToken);
            return new("invalid_request");
        }
        return await store.PutAsync(actor, traceId, capabilityId, regions.Order(StringComparer.Ordinal).ToArray(), verified, revision, cancellationToken);
    }
}
