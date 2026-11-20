using System.Text.Json;

namespace OpenConnect.Docs.Cloud;

internal static class CloudCapabilityPolicy
{
    internal static bool DependenciesEnabled(
        CloudCapabilityDefinition capability,
        IReadOnlyList<CloudCapabilityDefinition> capabilities,
        IReadOnlyDictionary<string, CloudTenantEntitlement> entitlements,
        IReadOnlyDictionary<string, CloudTenantCapabilitySelection> selections,
        DateTimeOffset now,
        IReadOnlyDictionary<string, CloudApplicationCapabilitySelection>? applicationSelections = null)
    {
        var byId = capabilities.ToDictionary(x => x.Id, StringComparer.Ordinal);
        return DependenciesEnabled(capability, byId, entitlements, selections, now, new HashSet<string>(StringComparer.Ordinal), applicationSelections);
    }

    internal static bool DependenciesEnabled(
        CloudCapabilityDefinition capability,
        IReadOnlyDictionary<string, CloudCapabilityDefinition> capabilities,
        IReadOnlyDictionary<string, CloudTenantEntitlement> entitlements,
        IReadOnlyDictionary<string, CloudTenantCapabilitySelection> selections,
        DateTimeOffset now,
        HashSet<string> path,
        IReadOnlyDictionary<string, CloudApplicationCapabilitySelection>? applicationSelections)
    {
        if (!path.Add(capability.Id)) return false;
        var dependencies = JsonSerializer.Deserialize<string[]>(capability.Dependencies) ?? [];
        var result = dependencies.All(id =>
        {
            if (!capabilities.TryGetValue(id, out var dependency)) return false;
            return dependency is not null && dependency.PlatformAvailability == "available" &&
                IsEntitled(entitlements.GetValueOrDefault(id), now) &&
                selections.TryGetValue(id, out var selection) && selection.Enabled &&
                (applicationSelections is null || applicationSelections.TryGetValue(id, out var appSelection) && appSelection.Enabled) &&
                DependenciesEnabled(dependency, capabilities, entitlements, selections, now, path, applicationSelections);
        });
        path.Remove(capability.Id);
        return result;
    }

    internal static bool IsEntitled(CloudTenantEntitlement? entitlement, DateTimeOffset now) =>
        entitlement is { PlanGranted: true } && (entitlement.ExpiresAt is null || entitlement.ExpiresAt > now);

    internal static string? GetReason(
        CloudCapabilityDefinition capability,
        bool entitled,
        bool tenantEnabled,
        bool applicationEnabled,
        bool dependencyReady,
        bool applicationSelectionRequired)
    {
        if (capability.PlatformAvailability != "available") return "capability_unavailable";
        if (!entitled) return "capability_not_entitled";
        if (!tenantEnabled) return "capability_disabled";
        if (!dependencyReady) return "dependency_not_enabled";
        if (applicationSelectionRequired && !applicationEnabled) return "capability_disabled";
        return null;
    }

}
