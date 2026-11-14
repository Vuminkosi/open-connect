using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.LiveUI;

namespace OpenConnect.LiveUI.Agents;

/// <summary>Typed agent operations. Model arguments never supply session, route, manifest hash, or action identity.</summary>
public sealed class LiveUIAgentTools(
    ILiveUIAgentContextAccessor contextAccessor,
    ILiveUIManifestProvider manifestProvider,
    ILiveUIActionAuthorizer authorizer,
    LiveUIManifestValidator manifestValidator,
    LiveUIOrchestrator orchestrator,
    IOptions<LiveUIOptions> options,
    ICapabilityRegistry? capabilityRegistry = null) : IDisposable
{
    private readonly SemaphoreSlim _actionGate = new(1, 1);

    public void Dispose() => _actionGate.Dispose();

    internal void RegisterCapability() => capabilityRegistry?.Register(
        new CapabilityDescriptor(new CapabilityId("liveui.agent-tools"), CapabilityKind.Tool, DetectorState.Ready));

    /// <summary>Returns only the authorized current route and its authorized compiled targets.</summary>
    public async ValueTask<LiveUIAgentPage?> GetCurrentPageAsync(CancellationToken cancellationToken = default)
    {
        var (context, manifest, route) = await GetCurrentRouteAsync(cancellationToken).ConfigureAwait(false);
        if (context is null || manifest is null || route is null) return null;

        var routeAction = CreateAction(context, manifest, route, null, LiveUIActionType.Inspect, new());
        var canInspectRoute = await IsAllowedAsync(
            new LiveUIExecutionContext(manifest, route, null, routeAction), cancellationToken).ConfigureAwait(false);

        var targets = new List<LiveUIAgentTarget>();
        foreach (var target in canInspectRoute ? route.Targets.Where(target => target.Capabilities.Count > 0) : [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            var authorizedCapabilities = new HashSet<LiveUICapability>();
            var capabilityGroups = target.Capabilities
                .Select(capability => (Capability: capability, Action: GetActionType(capability, target.Kind)))
                .Where(candidate => candidate.Action is not null)
                .GroupBy(candidate => candidate.Action!.Value);
            foreach (var group in capabilityGroups)
            {
                var candidate = CreateAction(context, manifest, route, target.Id, group.Key,
                    GetCapabilityProbeParameters(target, group.Key));
                var allowed = await IsAllowedAsync(
                    new LiveUIExecutionContext(manifest, route, target, candidate), cancellationToken).ConfigureAwait(false);
                if (allowed) authorizedCapabilities.UnionWith(group.Select(item => item.Capability));
            }

            if (authorizedCapabilities.Count > 0)
                targets.Add(new(target.Id, target.Kind.ToString(), target.Label, authorizedCapabilities, target.RequiresConfirmation));
        }

        var navigableRoutes = new List<LiveUIAgentRoute>();
        foreach (var destination in manifest.Routes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navigationAction = CreateAction(context, manifest, route, null, LiveUIActionType.Navigate,
                new(DestinationRouteId: destination.Id));
            if (await IsAllowedAsync(new LiveUIExecutionContext(manifest, route, null, navigationAction), cancellationToken)
                    .ConfigureAwait(false))
                navigableRoutes.Add(new(destination.Id, destination.Path, destination.Title));
        }

        return new LiveUIAgentPage(route.Id, route.Path, route.Title, manifest.BuildId, manifest.ContentHash, targets)
        {
            NavigableRoutes = navigableRoutes
        };
    }

    /// <summary>Navigates only to a route present in the current compiled manifest.</summary>
    public ValueTask<LiveUIActionReceipt> NavigateAsync(string routeId, CancellationToken cancellationToken = default) =>
        ExecuteOnCurrentRouteAsync(null, LiveUIActionType.Navigate,
            new LiveUIActionParameters(DestinationRouteId: routeId), cancellationToken);

    /// <summary>Runs a privacy-safe inspection against one compiled target.</summary>
    public ValueTask<LiveUIActionReceipt> InspectAsync(string targetId, CancellationToken cancellationToken = default) =>
        ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.Inspect, new(), cancellationToken);

    /// <summary>Executes one allowlisted LiveUI action against one compiled target.</summary>
    public ValueTask<LiveUIActionReceipt> ExecuteAsync(
        string targetId,
        string operation,
        string? value = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<LiveUIActionType>(operation, ignoreCase: true, out var type) ||
            type is not (LiveUIActionType.Open or LiveUIActionType.Close or LiveUIActionType.Click or
                LiveUIActionType.Focus or LiveUIActionType.Scroll or LiveUIActionType.SetValue or
                LiveUIActionType.Select or LiveUIActionType.SelectTab))
            return ValueTask.FromResult(Rejected("operation_unsupported"));

        return ExecuteOnCurrentRouteAsync(targetId, type, new(Value: value), cancellationToken);
    }

    /// <summary>Waits for a supported state on one compiled target.</summary>
    public ValueTask<LiveUIActionReceipt> AwaitStateAsync(
        string targetId,
        string stateName,
        string? stateValue = null,
        CancellationToken cancellationToken = default) =>
        ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.WaitForState,
            new(StateName: stateName, StateValue: stateValue), cancellationToken);

    /// <summary>Highlights a compiled target without changing application focus.</summary>
    public ValueTask<LiveUIActionReceipt> HighlightAsync(string targetId, CancellationToken cancellationToken = default) =>
        ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.Highlight, new(), cancellationToken);

    /// <summary>Clears LiveUI highlights without opening a Navigator tour.</summary>
    public ValueTask<LiveUIActionReceipt> ClearHighlightAsync(CancellationToken cancellationToken = default) =>
        ExecuteOnCurrentRouteAsync(null, LiveUIActionType.ClearHighlight, new(), cancellationToken);

    private async ValueTask<LiveUIActionReceipt> ExecuteOnCurrentRouteAsync(
        string? targetId,
        LiveUIActionType type,
        LiveUIActionParameters parameters,
        CancellationToken cancellationToken)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (context, manifest, route) = await GetCurrentRouteAsync(cancellationToken).ConfigureAwait(false);
            if (context is null || manifest is null || route is null) return Rejected("session_context_unavailable");
            var action = CreateAction(context, manifest, route, targetId, type, parameters);
            return await orchestrator.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _actionGate.Release();
        }
    }

    private async ValueTask<(LiveUIAgentContext? Context, LiveUIManifest? Manifest, LiveUIRoute? Route)> GetCurrentRouteAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var context = await contextAccessor.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (context is null || string.IsNullOrWhiteSpace(context.SessionId) || string.IsNullOrWhiteSpace(context.CurrentRouteId))
                return (null, null, null);

            var manifest = await manifestProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (manifestValidator.Validate(manifest).Count != 0) return (context, null, null);
            var route = manifest!.Routes.SingleOrDefault(candidate => candidate.Id == context.CurrentRouteId);
            return route is null ? (context, manifest, null) : (context, manifest, route);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, null, null);
        }
    }

    private LiveUIAction CreateAction(LiveUIAgentContext context, LiveUIManifest manifest, LiveUIRoute route,
        string? targetId, LiveUIActionType type, LiveUIActionParameters parameters) =>
        new(Guid.NewGuid().ToString("N"), LiveUISchema.CurrentVersion, context.SessionId, manifest.ContentHash,
            route.Id, targetId, type, parameters, options.Value.Interaction.DefaultActionTimeout);

    private static LiveUIActionType? GetActionType(LiveUICapability capability, LiveUITargetKind kind) => capability switch
    {
        LiveUICapability.Activate => kind == LiveUITargetKind.Tab ? LiveUIActionType.SelectTab : LiveUIActionType.Click,
        LiveUICapability.Close => LiveUIActionType.Close,
        LiveUICapability.Filter => kind switch
        {
            LiveUITargetKind.Input or LiveUITargetKind.Checkbox => LiveUIActionType.SetValue,
            LiveUITargetKind.Select => LiveUIActionType.Select,
            LiveUITargetKind.Tab => LiveUIActionType.SelectTab,
            _ => null
        },
        LiveUICapability.Focus => LiveUIActionType.Focus,
        LiveUICapability.Highlight => LiveUIActionType.Highlight,
        LiveUICapability.Inspect => LiveUIActionType.Inspect,
        LiveUICapability.Scroll => LiveUIActionType.Scroll,
        LiveUICapability.SetValue => LiveUIActionType.SetValue,
        LiveUICapability.Select => LiveUIActionType.Select,
        LiveUICapability.WaitForState => LiveUIActionType.WaitForState,
        _ => null
    };

    private static LiveUIActionParameters GetCapabilityProbeParameters(LiveUITarget target, LiveUIActionType actionType) =>
        actionType == LiveUIActionType.WaitForState && (target.Kind is LiveUITargetKind.List or LiveUITargetKind.Table)
            ? new(StateName: "textContains", StateValue: "probe")
            : new();

    private async ValueTask<bool> IsAllowedAsync(LiveUIExecutionContext context, CancellationToken cancellationToken)
    {
        try
        {
            return (await authorizer.AuthorizeAsync(context, cancellationToken).ConfigureAwait(false)).Allowed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static LiveUIActionReceipt Rejected(string code) =>
        new(Guid.NewGuid().ToString("N"), string.Empty, LiveUIActionOutcome.Denied, code, false, DateTimeOffset.UtcNow);
}

/// <summary>Authorized route metadata and targets exposed to the model as structured tool output.</summary>
public sealed record LiveUIAgentPage(
    string RouteId,
    string Path,
    string? Title,
    string BuildId,
    string ContentHash,
    IReadOnlyList<LiveUIAgentTarget> Targets)
{
    /// <summary>Routes the current host policy permits this session to navigate to.</summary>
    public IReadOnlyList<LiveUIAgentRoute> NavigableRoutes { get; init; } = [];
}

/// <summary>A manifest route that the current host policy permits the agent to navigate to.</summary>
public sealed record LiveUIAgentRoute(string RouteId, string Path, string? Title);

/// <summary>A compiled target with only the fields needed to propose a governed action.</summary>
public sealed record LiveUIAgentTarget(
    string TargetId,
    string Kind,
    string Label,
    IReadOnlySet<LiveUICapability> Capabilities,
    bool RequiresConfirmation);
