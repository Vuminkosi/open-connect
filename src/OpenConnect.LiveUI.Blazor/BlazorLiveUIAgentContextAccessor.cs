using Microsoft.AspNetCore.Components;
using OpenConnect.LiveUI.Agents;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Resolves session correlation and the current compiled route from the active Blazor circuit.</summary>
public sealed class BlazorLiveUIAgentContextAccessor(
    NavigationManager navigationManager,
    ILiveUIManifestProvider manifestProvider) : ILiveUIAgentContextAccessor, ILiveUIAgentContextSessionBinder
{
    private readonly string _circuitSessionId = Guid.NewGuid().ToString("N");
    private readonly AsyncLocal<string?> _boundSessionId = new();

    public IDisposable BindSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(sessionId));

        var previousSessionId = _boundSessionId.Value;
        _boundSessionId.Value = sessionId;
        return new SessionBinding(() => _boundSessionId.Value = previousSessionId);
    }

    public async ValueTask<LiveUIAgentContext?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manifest = await manifestProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (manifest is null) return null;

        var currentPath = navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath;
        var route = LiveUIRouteMatcher.Resolve(manifest.Routes, currentPath);
        return route is null ? null : new LiveUIAgentContext(_boundSessionId.Value ?? _circuitSessionId, route.Id)
        { CurrentPath = currentPath };
    }

    private sealed class SessionBinding(Action restore) : IDisposable
    {
        private Action? _restore = restore;

        public void Dispose() => Interlocked.Exchange(ref _restore, null)?.Invoke();
    }
}
