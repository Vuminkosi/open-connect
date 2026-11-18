using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Agents;

/// <summary>Identifies targets observed from the live page rather than compiled application metadata.</summary>
public static class LiveUIRuntimeTargetIds
{
    public const string Prefix = "oc.runtime.";
}

/// <summary>Refreshes runtime targets for an already authorized route in the active client circuit.</summary>
public interface ILiveUIRuntimeTargetDiscovery
{
    ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
        CancellationToken cancellationToken = default);
}
