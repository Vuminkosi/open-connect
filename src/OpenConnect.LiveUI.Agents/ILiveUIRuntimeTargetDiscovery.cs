using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Agents;

/// <summary>Identifies targets observed from the live page rather than compiled application metadata.</summary>
public static class LiveUIRuntimeTargetIds
{
    public const string Prefix = "oc.runtime.";
}

/// <summary>Identifies concrete routes observed from links on the active page.</summary>
public static class LiveUIRuntimeRouteIds
{
    public const string Prefix = "oc.runtime.route.";
}

/// <summary>Refreshes runtime targets for an already authorized route in the active client circuit.</summary>
public interface ILiveUIRuntimeTargetDiscovery
{
    ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
        CancellationToken cancellationToken = default);

    /// <summary>Checks that a runtime target belongs to the currently observed concrete route.</summary>
    bool IsCurrentTarget(LiveUIAgentContext context, LiveUIManifest manifest, string targetId) => false;

    /// <summary>Checks that an observed destination belongs to the current source page.</summary>
    bool IsCurrentRoute(LiveUIAgentContext context, LiveUIManifest manifest, string routeId) => false;
}
