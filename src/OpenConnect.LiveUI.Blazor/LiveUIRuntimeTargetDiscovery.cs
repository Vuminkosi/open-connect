using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;
using OpenConnect.LiveUI;
using OpenConnect.LiveUI.Agents;

namespace OpenConnect.LiveUI.Blazor;

internal sealed class LiveUIRuntimeTargetDiscovery(
    IJSRuntime jsRuntime,
    NavigationManager navigationManager,
    LiveUIManifestState state,
    IOptions<LiveUIOptions> options) : ILiveUIRuntimeTargetDiscovery, IAsyncDisposable
{
    private IJSObjectReference? _module;

    public async ValueTask<bool> RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
        CancellationToken cancellationToken = default)
    {
        var expectedPath = context.CurrentPath;
        if (expectedPath is null || !LiveUIRouteMatcher.TryMatch(route.Path, expectedPath, out _) ||
            !string.Equals(navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath, expectedPath, StringComparison.Ordinal)) return false;
        try
        {
            _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken,
                "./_content/OpenConnect.LiveUI.Blazor/liveui.js").ConfigureAwait(false);
            var maximum = Math.Clamp(options.Value.Discovery.MaximumTargetsPerRoute, 0, 500);
            var observed = await _module.InvokeAsync<ObservedTarget[]>("discoverTargets", cancellationToken, maximum).ConfigureAwait(false);
            if (observed is null) return false;
            if (!string.Equals(navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath, expectedPath, StringComparison.Ordinal)) return false;

            List<LiveUITarget> existing = options.Value.Discovery.Mode == LiveUIDiscoveryMode.RuntimeOnly
                ? []
                : route.Targets.Where(target => !target.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal)).ToList();
            var known = existing.Select(target => Key(target.Kind, target.Role, target.Label, target.ParentId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in observed.Take(maximum))
            {
                if (existing.Count >= maximum) break;
                if (candidate is null || !Enum.TryParse<LiveUITargetKind>(candidate.Kind, out var kind) ||
                    candidate.Label is not { Length: > 0 and <= 120 } label || label.Any(char.IsControl) ||
                    candidate.Role is { Length: > 32 } || candidate.ParentLabel is { Length: > 120 }) continue;
                string? parentId = null;
                if (candidate.ParentLabel is { Length: > 0 } parentLabel)
                {
                    var parents = existing.Where(target => target.Kind == LiveUITargetKind.Region &&
                        string.Equals(target.Label, parentLabel, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
                    if (parents.Length != 1) continue;
                    parentId = parents[0].Id;
                }
                if (!known.Add(Key(kind, candidate.Role, label, parentId))) continue;
                var capabilities = Capabilities(kind);
                if (capabilities.Count == 0) continue;
                var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(route.Id + "\0" + Key(kind, candidate.Role, label, parentId)));
                existing.Add(new LiveUITarget(LiveUIRuntimeTargetIds.Prefix + Convert.ToHexString(idBytes).ToLowerInvariant(), kind, label,
                    capabilities, candidate.Role, ParentId: parentId,
                    RequiresConfirmation: kind is not (LiveUITargetKind.Heading or LiveUITargetKind.Region)));
            }
            var routes = manifest.Routes.Select(item => item.Id == route.Id ? item with { Targets = existing.ToArray() } : item).ToArray();
            var replacement = manifest with { Routes = routes, ContentHash = string.Empty };
            replacement = replacement with { ContentHash = LiveUIManifestHasher.ComputeContentHash(replacement) };
            return state.TryReplace(manifest, replacement, route.Id, expectedPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JSException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    public bool IsCurrentTarget(LiveUIAgentContext context, LiveUIManifest manifest, string targetId) =>
        state.IsRuntimeTargetCurrent(context, manifest, targetId);

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
        }
    }

    private static string Key(LiveUITargetKind kind, string? role, string label, string? parentId) =>
        $"{kind}\0{role}\0{label.Trim().ToUpperInvariant()}\0{parentId}";

    private static HashSet<LiveUICapability> Capabilities(LiveUITargetKind kind) => kind switch
    {
        LiveUITargetKind.Button or LiveUITargetKind.Link or LiveUITargetKind.Tab =>
            [LiveUICapability.Activate, LiveUICapability.Focus, LiveUICapability.Highlight, LiveUICapability.Inspect, LiveUICapability.WaitForState],
        LiveUITargetKind.Input => [LiveUICapability.SetValue, LiveUICapability.Focus, LiveUICapability.Inspect, LiveUICapability.WaitForState],
        LiveUITargetKind.Select => [LiveUICapability.Select, LiveUICapability.Focus, LiveUICapability.Inspect, LiveUICapability.WaitForState],
        LiveUITargetKind.Heading or LiveUITargetKind.Region => [LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll],
        _ => []
    };

    private sealed record ObservedTarget(string Kind, string Label, string? Role, string? ParentLabel = null);
}
