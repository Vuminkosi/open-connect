using OpenConnect.LiveUI.Agents;
using OpenConnect.LiveUI.Inference;
using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Resolves an authenticated application session to its privacy egress context.</summary>
public interface ICloudLiveUISessionContextProvider
{
    /// <summary>Returns null unless the current principal owns the supplied session.</summary>
    ValueTask<EgressContext?> GetContextAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>Reads a page that has passed the host's LiveUI authorization checks.</summary>
public interface ICloudLiveUIPageProvider
{
    ValueTask<LiveUIAgentPage?> GetCurrentPageAsync(CancellationToken cancellationToken = default);
}

/// <summary>Reads the current host-authorized page through LiveUI agent tools.</summary>
public sealed class CloudLiveUIPageProvider(LiveUIAgentTools tools) : ICloudLiveUIPageProvider
{
    public ValueTask<LiveUIAgentPage?> GetCurrentPageAsync(CancellationToken cancellationToken = default) =>
        tools.GetCurrentPageAsync(cancellationToken);
}

/// <summary>Revalidates the current session, route, manifest and target capabilities before Cloud egress.</summary>
public sealed class CloudLiveUIHostAuthorizer(
    ILiveUIAgentContextAccessor currentContext,
    ILiveUIManifestProvider manifests,
    ICloudLiveUIPageProvider pages,
    ICloudLiveUISessionContextProvider sessions) : ICloudLiveUIHostAuthorizer
{
    public async ValueTask<EgressContext?> AuthorizeAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var before = await currentContext.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (before is null || before.SessionId != request.SessionId || before.CurrentRouteId != request.RouteId)
            return null;
        var identity = await sessions.GetContextAsync(before.SessionId, cancellationToken).ConfigureAwait(false);
        if (identity is null || identity.Destination != EgressDestination.ExternalModel ||
            identity.Session.Value.ToString() != before.SessionId)
            return null;
        var page = await pages.GetCurrentPageAsync(cancellationToken).ConfigureAwait(false);
        var manifest = await manifests.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (page is null || manifest is null || manifest.SchemaVersion != request.SchemaVersion ||
            manifest.ContentHash != page.ContentHash || !LiveUIInferenceCapabilities.IsAuthorized(request, page))
            return null;
        var after = await currentContext.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (after is null || after.SessionId != before.SessionId || after.CurrentRouteId != before.CurrentRouteId ||
            !string.Equals(after.CurrentPath, before.CurrentPath, StringComparison.Ordinal))
            return null;
        var currentIdentity = await sessions.GetContextAsync(before.SessionId, cancellationToken).ConfigureAwait(false);
        return currentIdentity == identity ? identity : null;
    }
}
