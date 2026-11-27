using static OpenConnect.Docs.Cloud.CloudCapabilityPolicy;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudCapabilityView(
    string Id,
    string Name,
    string Description,
    string ContractVersion,
    string BillingUnit,
    string PlatformAvailability,
    IReadOnlyList<string> Dependencies,
    IReadOnlyList<string> Modalities,
    bool ModalitiesVerified,
    IReadOnlyList<string> AllowedRegions,
    bool AllowedRegionsVerified,
    bool? Entitled,
    bool? TenantEnabled,
    bool? ApplicationEnabled,
    int? Revision,
    string? ReasonCode);

public sealed record CloudCapabilitySelectionRequest(bool Enabled);
public sealed record CloudCapabilitySelectionResult(string Status, int? Revision = null);

public sealed class CloudCapabilityService(ICloudCapabilityReader reader, ICloudCapabilitySelectionStore selections, TimeProvider timeProvider)
{
    private static readonly string[] Administrators = ["Owner", "Administrator"];

    public async Task<IReadOnlyList<CloudCapabilityView>> GetCatalogAsync(CancellationToken cancellationToken) =>
        CloudCapabilityProjection.Catalog(await reader.GetCatalogAsync(cancellationToken));

    public async Task<IReadOnlyList<CloudCapabilityView>?> GetTenantCapabilitiesAsync(
        CloudSubject subject, Guid tenantId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.GetTenantAsync(subject, tenantId, cancellationToken);
        return snapshot is null ? null : CloudCapabilityProjection.Tenant(snapshot, timeProvider.GetUtcNow());
    }

    public async Task<IReadOnlyList<CloudCapabilityView>?> GetApplicationCapabilitiesAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        var snapshot = await reader.GetApplicationAsync(subject, tenantId, applicationId, cancellationToken);
        return snapshot is null ? null : CloudCapabilityProjection.Application(snapshot, timeProvider.GetUtcNow());
    }

    public Task<CloudCapabilitySelectionResult> SetTenantSelectionAsync(
        CloudSubject subject, Guid tenantId, string capabilityId, bool enabled, int expectedRevision, CancellationToken cancellationToken) =>
        SetSelectionAsync(subject, tenantId, null, capabilityId, enabled, expectedRevision, cancellationToken);

    public Task<CloudCapabilitySelectionResult> SetApplicationSelectionAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, string capabilityId, bool enabled, int expectedRevision, CancellationToken cancellationToken) =>
        SetSelectionAsync(subject, tenantId, applicationId, capabilityId, enabled, expectedRevision, cancellationToken);

    private Task<CloudCapabilitySelectionResult> SetSelectionAsync(
        CloudSubject subject, Guid tenantId, Guid? applicationId, string capabilityId, bool enabled, int expectedRevision, CancellationToken cancellationToken) =>
        selections.ChangeAsync(subject, tenantId, applicationId, capabilityId, enabled,
            role => Administrators.Contains(role, StringComparer.Ordinal),
            snapshot => EvaluateSelection(snapshot, applicationId is not null, capabilityId, enabled, expectedRevision, timeProvider.GetUtcNow()),
            cancellationToken);

    private static CloudCapabilitySelectionResult EvaluateSelection(
        CloudCapabilitySnapshot snapshot, bool applicationSelection, string capabilityId, bool enabled, int expectedRevision, DateTimeOffset now)
    {
        var capability = snapshot.Capabilities.FirstOrDefault(x => x.Id == capabilityId);
        if (capability is null)
            return new("not_found");
        if (enabled)
        {
            if (!IsEntitled(snapshot.Entitlements.GetValueOrDefault(capabilityId), now))
                return new("capability_not_entitled");
            if (!string.Equals(capability.PlatformAvailability, "available", StringComparison.Ordinal))
                return new("capability_unavailable");
            if (!DependenciesEnabled(capability, snapshot.Capabilities, snapshot.Entitlements, snapshot.TenantSelections, now,
                    applicationSelection ? snapshot.ApplicationSelections : null))
                return new("dependency_not_enabled");
            if (applicationSelection && (!snapshot.TenantSelections.TryGetValue(capabilityId, out var tenantSelection) || !tenantSelection.Enabled))
                return new("capability_disabled");
        }

        var revision = applicationSelection
            ? snapshot.ApplicationSelections!.GetValueOrDefault(capabilityId)?.Revision ?? 0
            : snapshot.TenantSelections.GetValueOrDefault(capabilityId)?.Revision ?? 0;
        return revision != expectedRevision ? new("conflict", revision) : new("succeeded", revision + 1);
    }
}
