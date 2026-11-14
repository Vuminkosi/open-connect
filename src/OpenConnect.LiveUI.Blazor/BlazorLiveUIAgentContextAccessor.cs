using Microsoft.AspNetCore.Components;
using OpenConnect.LiveUI.Agents;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Resolves session correlation and the current compiled route from the active Blazor circuit.</summary>
public sealed class BlazorLiveUIAgentContextAccessor(
    NavigationManager navigationManager,
    ILiveUIManifestProvider manifestProvider) : ILiveUIAgentContextAccessor
{
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public async ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = await manifestProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null) return null;

        var currentPath = navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath;
        var route = manifest.Routes.SingleOrDefault(candidate =>
            string.Equals(navigationManager.ToAbsoluteUri(candidate.Path).AbsolutePath, currentPath,
                StringComparison.OrdinalIgnoreCase));
        return route is null ? null : new LiveUIAgentContext(_sessionId, route.Id);
    }
}
