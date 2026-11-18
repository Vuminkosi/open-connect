using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;

namespace OpenConnect.LiveUI.Blazor;

public sealed class LiveUIManifestState(LiveUIManifestValidator validator) : ILiveUIManifestProvider
{
    private readonly object _gate = new();
    private LiveUIManifest? _manifest;
    private readonly Dictionary<string, string> _runtimePaths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string RouteId, string Path, string? ParentLabel)> _runtimeDestinations = new(StringComparer.Ordinal);

    public void SetManifest(LiveUIManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var errors = validator.Validate(manifest);
        if (errors.Count != 0)
            throw new ArgumentException($"The LiveUI manifest is invalid ({errors[0]}).", nameof(manifest));
        lock (_gate)
        {
            _manifest = manifest;
            _runtimePaths.Clear();
            _runtimeDestinations.Clear();
        }
    }

    public ValueTask<LiveUIManifest?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) return ValueTask.FromResult(_manifest);
    }

    internal bool TryReplace(LiveUIManifest expected, LiveUIManifest replacement, string routeId, string path,
        IReadOnlyDictionary<string, string?> destinationParents)
    {
        if (validator.Validate(replacement).Count != 0) return false;
        lock (_gate)
        {
            if (!ReferenceEquals(_manifest, expected)) return false;
            _manifest = replacement;
            _runtimePaths[routeId] = path;
            _runtimeDestinations.Clear();
            foreach (var route in replacement.Routes.Where(route => route.Id != routeId &&
                route.Id.StartsWith(LiveUIRuntimeRouteIds.Prefix, StringComparison.Ordinal)))
                if (destinationParents.TryGetValue(route.Id, out var parentLabel))
                    _runtimeDestinations[route.Id] = (routeId, path, parentLabel);
            return true;
        }
    }

    internal bool IsRuntimeRouteCurrent(LiveUIAgentContext context, LiveUIManifest manifest, string destinationId)
        => TryGetRuntimeRouteContext(context, manifest, destinationId, out _);

    internal bool TryGetRuntimeRouteContext(LiveUIAgentContext context, LiveUIManifest manifest, string destinationId, out string? parentLabel)
    {
        lock (_gate)
        {
            parentLabel = null;
            if (!(ReferenceEquals(_manifest, manifest) && context.CurrentPath is not null &&
                _runtimeDestinations.TryGetValue(destinationId, out var source) &&
                source.RouteId == context.CurrentRouteId && string.Equals(source.Path, context.CurrentPath, StringComparison.Ordinal))) return false;
            parentLabel = source.ParentLabel;
            return true;
        }
    }

    internal bool IsRuntimeTargetCurrent(LiveUIAgentContext context, LiveUIManifest manifest, string targetId)
    {
        lock (_gate)
            return ReferenceEquals(_manifest, manifest) && context.CurrentPath is not null &&
                _runtimePaths.TryGetValue(context.CurrentRouteId, out var path) &&
                string.Equals(path, context.CurrentPath, StringComparison.Ordinal) &&
                manifest.Routes.Any(route => route.Id == context.CurrentRouteId && route.Targets.Any(target => target.Id == targetId));
    }
}
