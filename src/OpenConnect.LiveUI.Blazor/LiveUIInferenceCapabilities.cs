using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Inference;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Maps an authorized LiveUI page to the capabilities visible to an inference provider.</summary>
public static class LiveUIInferenceCapabilities
{
    /// <summary>Builds the proposal allowlist from a host-authorized page.</summary>
    public static IReadOnlyList<LiveUIInferenceCapability> FromPage(LiveUIAgentPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.Targets.Select(target => new LiveUIInferenceCapability(target.TargetId,
                target.Capabilities.SelectMany(capability => ToActions(capability, target.Kind)).Distinct(StringComparer.Ordinal).ToArray())
                { Label = BoundedLabel(target.Label) })
            .Where(capability => capability.Actions.Count > 0)
            .Concat(page.NavigableRoutes.Select(route => new LiveUIInferenceCapability(route.RouteId, ["navigate"])
                { Label = BoundedLabel(route.Title) }))
            .ToArray();
    }

    /// <summary>Checks that requested targets, actions and labels are still authorized for the current page.</summary>
    public static bool IsAuthorized(LiveUIInferenceRequest request, LiveUIAgentPage page)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(page);
        if (request.RouteId != page.RouteId || request.ManifestHash != page.ContentHash || request.Capabilities is null) return false;
        var current = FromPage(page);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requested in request.Capabilities)
        {
            if (requested is null || requested.Actions is null || !seen.Add(requested.TargetId)) return false;
            var authorized = current.Where(item => item.TargetId == requested.TargetId).ToArray();
            if (authorized.Length != 1 || !string.Equals(authorized[0].Label, requested.Label, StringComparison.Ordinal) ||
                requested.Actions.Count == 0 || requested.Actions.Any(action => !authorized[0].Actions.Contains(action, StringComparer.Ordinal))) return false;
        }
        return true;
    }

    private static string? BoundedLabel(string? label) =>
        string.IsNullOrWhiteSpace(label) ? null : label.Length <= 256 ? label : label[..256];

    private static IEnumerable<string> ToActions(LiveUICapability capability, string targetKind)
    {
        if (!Enum.TryParse<LiveUITargetKind>(targetKind, ignoreCase: true, out var kind)) return [];
        return capability switch
        {
            LiveUICapability.Activate => ["activate"],
            LiveUICapability.Filter when kind == LiveUITargetKind.Tab => ["activate"],
            LiveUICapability.Filter when kind == LiveUITargetKind.Select => ["select"],
            LiveUICapability.Filter when kind is LiveUITargetKind.Input or LiveUITargetKind.Checkbox => ["setValue"],
            LiveUICapability.SetValue => ["setValue"],
            LiveUICapability.Select => ["select"],
            LiveUICapability.Highlight => ["highlight"],
            LiveUICapability.Focus => ["focus"],
            LiveUICapability.Scroll => ["scroll"],
            LiveUICapability.Inspect => ["inspect"],
            _ => []
        };
    }
}
