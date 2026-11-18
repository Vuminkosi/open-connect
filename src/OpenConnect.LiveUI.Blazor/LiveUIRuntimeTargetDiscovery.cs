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

    public async ValueTask RefreshAsync(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
        CancellationToken cancellationToken = default)
    {
        var expectedPath = context.CurrentPath;
        if (expectedPath is null || !LiveUIRouteMatcher.TryMatch(route.Path, expectedPath, out _) ||
            !string.Equals(navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            _module ??= await jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken,
                "./_content/OpenConnect.LiveUI.Blazor/liveui.js").ConfigureAwait(false);
            var maximum = Math.Clamp(options.Value.Discovery.MaximumTargetsPerRoute, 0, 500);
            var observed = await _module.InvokeAsync<ObservedTarget[]>("discoverTargets", cancellationToken, maximum).ConfigureAwait(false);
            if (observed is null) return;
            if (!string.Equals(navigationManager.ToAbsoluteUri(navigationManager.Uri).AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase)) return;

            var existing = route.Targets.Where(target => !target.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal)).ToList();
            var known = existing.Select(target => Key(target.Kind, target.Role, target.Label)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in observed.Take(Math.Max(0, maximum - existing.Count)))
            {
                if (candidate is null || !Enum.TryParse<LiveUITargetKind>(candidate.Kind, out var kind) ||
                    candidate.Label is not { Length: > 0 and <= 120 } label || label.Any(char.IsControl) ||
                    candidate.Role is { Length: > 32 } || !known.Add(Key(kind, candidate.Role, label))) continue;
                var capabilities = Capabilities(kind);
                if (capabilities.Count == 0) continue;
                var idBytes = SHA256.HashData(Encoding.UTF8.GetBytes(route.Id + "\0" + Key(kind, candidate.Role, label)));
                existing.Add(new LiveUITarget(LiveUIRuntimeTargetIds.Prefix + Convert.ToHexString(idBytes).ToLowerInvariant(), kind, label,
                    capabilities, candidate.Role, RequiresConfirmation: kind != LiveUITargetKind.Heading));
            }
            var routes = manifest.Routes.Select(item => item.Id == route.Id ? item with { Targets = existing.ToArray() } : item).ToArray();
            var replacement = manifest with { Routes = routes, ContentHash = string.Empty };
            replacement = replacement with { ContentHash = LiveUIManifestHasher.ComputeContentHash(replacement) };
            state.TryReplace(manifest, replacement);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try { await _module.DisposeAsync().ConfigureAwait(false); }
            catch (JSDisconnectedException) { }
        }
    }

    private static string Key(LiveUITargetKind kind, string? role, string label) =>
        $"{kind}\0{role}\0{label.Trim().ToUpperInvariant()}";

    private static HashSet<LiveUICapability> Capabilities(LiveUITargetKind kind) => kind switch
    {
        LiveUITargetKind.Button or LiveUITargetKind.Link or LiveUITargetKind.Tab =>
            [LiveUICapability.Activate, LiveUICapability.Focus, LiveUICapability.Highlight, LiveUICapability.Inspect, LiveUICapability.WaitForState],
        LiveUITargetKind.Input => [LiveUICapability.SetValue, LiveUICapability.Focus, LiveUICapability.Inspect, LiveUICapability.WaitForState],
        LiveUITargetKind.Select => [LiveUICapability.Select, LiveUICapability.Focus, LiveUICapability.Inspect, LiveUICapability.WaitForState],
        LiveUITargetKind.Heading => [LiveUICapability.Inspect, LiveUICapability.Highlight, LiveUICapability.Scroll],
        _ => []
    };

    private sealed record ObservedTarget(string Kind, string Label, string? Role);
}
