using System.Text.Json;
using static OpenConnect.Docs.Cloud.CloudCapabilityPolicy;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudCapabilitySnapshot(
    IReadOnlyList<CloudCapabilityDefinition> Capabilities,
    IReadOnlyDictionary<string, CloudTenantEntitlement> Entitlements,
    IReadOnlyDictionary<string, CloudTenantCapabilitySelection> TenantSelections,
    IReadOnlyDictionary<string, CloudApplicationCapabilitySelection>? ApplicationSelections);

public interface ICloudCapabilityReader
{
    Task<IReadOnlyList<CloudCapabilityDefinition>> GetCatalogAsync(CancellationToken cancellationToken);
    Task<CloudCapabilitySnapshot?> GetTenantAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken);
    Task<CloudCapabilitySnapshot?> GetApplicationAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken);
}

internal static class CloudCapabilityProjection
{
    internal static IReadOnlyList<CloudCapabilityView> Catalog(IReadOnlyList<CloudCapabilityDefinition> capabilities) =>
        capabilities.Select(capability => ToView(capability, null, null, null, null,
            capability.PlatformAvailability == "available" ? null : "capability_unavailable")).ToArray();

    internal static IReadOnlyList<CloudCapabilityView> Tenant(CloudCapabilitySnapshot snapshot, DateTimeOffset now) =>
        snapshot.Capabilities.Select(capability =>
        {
            var entitled = IsEntitled(snapshot.Entitlements.GetValueOrDefault(capability.Id), now);
            var enabled = snapshot.TenantSelections.TryGetValue(capability.Id, out var selection) && selection.Enabled;
            var dependencies = DependenciesEnabled(capability, snapshot.Capabilities, snapshot.Entitlements, snapshot.TenantSelections, now);
            return ToView(capability, entitled, enabled, false, selection?.Revision,
                GetReason(capability, entitled, enabled, false, dependencies, applicationSelectionRequired: false));
        }).ToArray();

    internal static IReadOnlyList<CloudCapabilityView> Application(CloudCapabilitySnapshot snapshot, DateTimeOffset now)
    {
        var applicationSelections = snapshot.ApplicationSelections ?? throw new InvalidOperationException("Application selections are required.");
        return snapshot.Capabilities.Select(capability =>
        {
            var entitled = IsEntitled(snapshot.Entitlements.GetValueOrDefault(capability.Id), now);
            var tenantEnabled = snapshot.TenantSelections.TryGetValue(capability.Id, out var tenantSelection) && tenantSelection.Enabled;
            var appEnabled = applicationSelections.TryGetValue(capability.Id, out var appSelection) && appSelection.Enabled;
            var dependencies = DependenciesEnabled(capability, snapshot.Capabilities, snapshot.Entitlements, snapshot.TenantSelections, now, applicationSelections);
            return ToView(capability, entitled, tenantEnabled, appEnabled, appSelection?.Revision,
                GetReason(capability, entitled, tenantEnabled, appEnabled, dependencies, applicationSelectionRequired: true));
        }).ToArray();
    }

    private static CloudCapabilityView ToView(
        CloudCapabilityDefinition capability, bool? entitled, bool? tenantEnabled, bool? applicationEnabled, int? revision, string? reason) => new(
            capability.Id, capability.Name, capability.Description, capability.ContractVersion, capability.BillingUnit,
            capability.PlatformAvailability, JsonSerializer.Deserialize<string[]>(capability.Dependencies) ?? [],
            JsonSerializer.Deserialize<string[]>(capability.Modalities) ?? [], capability.ModalitiesVerified,
            JsonSerializer.Deserialize<string[]>(capability.AllowedRegions) ?? [], capability.AllowedRegionsVerified,
            entitled, tenantEnabled, applicationEnabled, revision, reason);
}
