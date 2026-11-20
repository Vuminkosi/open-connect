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
    ICapabilityRegistry? capabilityRegistry = null,
    ILiveUIRuntimeTargetDiscovery? runtimeDiscovery = null) : IDisposable
{
    private readonly SemaphoreSlim _actionGate = new(1, 1);
    private readonly object _pageGate = new();
    private readonly Dictionary<string, (string RouteId, string Path)> _observedPages = new(StringComparer.Ordinal);

    public void Dispose() => _actionGate.Dispose();

    internal void RegisterCapability() => capabilityRegistry?.Register(
        new CapabilityDescriptor(new CapabilityId("liveui.agent-tools"), CapabilityKind.Tool, DetectorState.Ready));

    /// <summary>Returns only the authorized current route and its authorized compiled targets.</summary>
    public ValueTask<LiveUIAgentPage?> GetCurrentPageAsync(CancellationToken cancellationToken = default) =>
        GetCurrentPageCoreAsync(true, cancellationToken);

    private async ValueTask<LiveUIAgentPage?> GetCurrentPageCoreAsync(bool discover, CancellationToken cancellationToken)
    {
        var (context, manifest, route) = await GetCurrentRouteAsync(cancellationToken).ConfigureAwait(false);
        if (context is null || manifest is null || route is null)
            return null;

        var routeAction = CreateAction(context, manifest, route, null, LiveUIActionType.Inspect, new());
        var canInspectRoute = await IsAllowedAsync(
            new LiveUIExecutionContext(manifest, route, null, routeAction), cancellationToken).ConfigureAwait(false);

        var runtimeOnly = options.Value.Discovery.Mode == LiveUIDiscoveryMode.RuntimeOnly;
        var runtimeFresh = false;
        if (discover && canInspectRoute && options.Value.Discovery.EnableRuntimeFallback && runtimeDiscovery is not null)
        {
            var refreshed = await runtimeDiscovery.RefreshAsync(context, manifest, route, cancellationToken).ConfigureAwait(false);
            if (!refreshed && runtimeOnly)
                return null;
            runtimeFresh = refreshed;
            (context, manifest, route) = await GetCurrentRouteAsync(cancellationToken).ConfigureAwait(false);
            if (context is null || manifest is null || route is null)
                return null;
            routeAction = CreateAction(context, manifest, route, null, LiveUIActionType.Inspect, new());
            canInspectRoute = await IsAllowedAsync(
                new LiveUIExecutionContext(manifest, route, null, routeAction), cancellationToken).ConfigureAwait(false);
        }
        else if (runtimeOnly && canInspectRoute)
            return null;

        var targets = new List<LiveUIAgentTarget>();
        foreach (var target in canInspectRoute ? route.Targets.Where(target => target.Capabilities.Count > 0 &&
            (!target.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal) ||
             runtimeFresh && runtimeDiscovery?.IsCurrentTarget(context, manifest, target.Id) == true)) : [])
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
                if (allowed)
                    authorizedCapabilities.UnionWith(group.Select(item => item.Capability));
            }

            if (authorizedCapabilities.Count > 0)
                targets.Add(new(target.Id, target.Kind.ToString(), target.Label, authorizedCapabilities, target.RequiresConfirmation)
                {
                    LabelMayBeInferred = target.Kind == LiveUITargetKind.Checkbox &&
                        target.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal) ||
                        target.Kind == LiveUITargetKind.List &&
                        target.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix + "inferred-list.", StringComparison.Ordinal)
                });
        }

        var authorizedTargets = targets.ToDictionary(target => target.TargetId, StringComparer.Ordinal);
        var routeTargets = route.Targets.ToDictionary(target => target.Id, StringComparer.Ordinal);
        targets = targets.Where(target => routeTargets[target.TargetId].ParentId is not { } parentId || authorizedTargets.ContainsKey(parentId))
            .Select(target => routeTargets[target.TargetId].ParentId is { } parentId
                ? target with { ParentLabel = authorizedTargets[parentId].Label }
                : target).ToList();

        var navigableRoutes = new List<LiveUIAgentRoute>();
        foreach (var destination in manifest.Routes.Where(destination =>
            !destination.Path.Contains('{') && !destination.Path.Contains('}') &&
            (!destination.Id.StartsWith(LiveUIRuntimeRouteIds.Prefix, StringComparison.Ordinal) ||
             runtimeFresh && runtimeDiscovery?.IsCurrentRoute(context, manifest, destination.Id) == true)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var navigationAction = CreateAction(context, manifest, route, null, LiveUIActionType.Navigate,
                new(DestinationRouteId: destination.Id));
            if (await IsAllowedAsync(new LiveUIExecutionContext(manifest, route, null, navigationAction), cancellationToken)
                    .ConfigureAwait(false))
                navigableRoutes.Add(new(destination.Id, destination.Path, destination.Title));
        }

        if (context.CurrentPath is not null)
        {
            var latest = await contextAccessor.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (latest is null || latest.SessionId != context.SessionId || latest.CurrentRouteId != context.CurrentRouteId ||
                !string.Equals(latest.CurrentPath, context.CurrentPath, StringComparison.Ordinal))
                return null;
            lock (_pageGate)
            {
                if (!_observedPages.ContainsKey(context.SessionId) && _observedPages.Count >= 256)
                    return null;
                _observedPages[context.SessionId] = (context.CurrentRouteId, context.CurrentPath);
            }
        }

        return new LiveUIAgentPage(route.Id, route.Path, route.Title, manifest.BuildId, manifest.ContentHash, targets)
        {
            NavigableRoutes = navigableRoutes
        };
    }

    // Direct tool calls have no authenticated gateway privacy context. Keep runtime-observed
    // page labels out of that model path until an equivalent host egress gate is available.
    /// <summary>Returns page metadata without runtime-observed labels for paths without a trusted egress context.</summary>
    public async ValueTask<LiveUIAgentPage?> GetCurrentPageForDirectAgentAsync(CancellationToken cancellationToken = default)
    {
        var page = await GetCurrentPageCoreAsync(false, cancellationToken).ConfigureAwait(false);
        if (page is null)
            return null;
        if (page.RouteId.StartsWith(LiveUIRuntimeRouteIds.Prefix, StringComparison.Ordinal))
            return null;
        var filteredTargets = page.Targets.Where(target => !target.TargetId.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal)).ToArray();
        var manifest = await manifestProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var hasRuntimeTargets = manifest?.Routes.Any(route => route.Id.StartsWith(LiveUIRuntimeRouteIds.Prefix, StringComparison.Ordinal) ||
            route.Targets.Any(target => target.Id.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal))) != false;
        return page with
        {
            Targets = filteredTargets,
            NavigableRoutes = page.NavigableRoutes.Where(route => !route.RouteId.StartsWith(LiveUIRuntimeRouteIds.Prefix, StringComparison.Ordinal)).ToArray(),
            ContentHash = hasRuntimeTargets || filteredTargets.Length != page.Targets.Count ? string.Empty : page.ContentHash
        };
    }

    /// <summary>Navigates only to an authorized route present in the current manifest snapshot.</summary>
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
        if (string.Equals(operation, "activate", StringComparison.OrdinalIgnoreCase))
        {
            if (value is not null)
                return ValueTask.FromResult(Rejected("operation_unsupported"));
            return ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.Click, new(), cancellationToken, resolveActivate: true);
        }

        if (!Enum.TryParse<LiveUIActionType>(operation, ignoreCase: true, out var type) ||
            type is not (LiveUIActionType.Open or LiveUIActionType.Close or LiveUIActionType.Click or
                LiveUIActionType.Focus or LiveUIActionType.Scroll or LiveUIActionType.SetValue or
                LiveUIActionType.Select or LiveUIActionType.SelectTab))
            return ValueTask.FromResult(Rejected("operation_unsupported"));

        return ExecuteOnCurrentRouteAsync(targetId, type, new(Value: value), cancellationToken);
    }

    /// <summary>Executes a supported activation and verifies a state change on the same target.</summary>
    public ValueTask<LiveUIActionReceipt> ExecuteWithPostconditionAsync(
        string targetId, string operation, string stateName, string? stateValue = null,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(operation, "activate", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(stateName) || stateName.Length > 32 || stateValue is { Length: > 256 })
                return ValueTask.FromResult(Rejected("invalid_request"));
            return ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.Click,
                new(StateName: stateName, StateValue: stateValue), cancellationToken, resolveActivate: true);
        }
        if (!Enum.TryParse<LiveUIActionType>(operation, ignoreCase: true, out var type) ||
            type is not (LiveUIActionType.Click or LiveUIActionType.Open or LiveUIActionType.Close) ||
            string.IsNullOrWhiteSpace(stateName) || stateName.Length > 32 || stateValue is { Length: > 256 })
            return ValueTask.FromResult(Rejected("invalid_request"));
        return ExecuteOnCurrentRouteAsync(targetId, type, new(StateName: stateName, StateValue: stateValue), cancellationToken);
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
    /// <param name="targetId">The compiled target identifier.</param>
    /// <param name="cancellationToken">Cancels the interaction request.</param>
    /// <param name="durationMilliseconds">Optional temporary highlight duration, bounded to 30 seconds by the runtime. Ignored when speech synchronization is enabled so playback events control cleanup.</param>
    public ValueTask<LiveUIActionReceipt> HighlightAsync(
        string targetId,
        CancellationToken cancellationToken = default,
        int durationMilliseconds = 0)
    {
        var effectiveDuration = options.Value.Narration.SynchronizeWithSpeech ? 0 : durationMilliseconds;
        return ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.Highlight,
            new(HighlightDurationMilliseconds: effectiveDuration), cancellationToken);
    }

    /// <summary>Highlights the unique visible row containing the supplied text inside an authorized list or table target.</summary>
    public ValueTask<LiveUIActionReceipt> HighlightTextAsync(
        string targetId,
        string matchText,
        CancellationToken cancellationToken = default,
        int durationMilliseconds = 0)
    {
        if (string.IsNullOrWhiteSpace(matchText) || matchText.Length > 256)
            return ValueTask.FromResult(Rejected("invalid_highlight_match_text"));

        var effectiveDuration = options.Value.Narration.SynchronizeWithSpeech ? 0 : durationMilliseconds;
        return ExecuteOnCurrentRouteAsync(targetId, LiveUIActionType.Highlight,
            new(Value: matchText, HighlightDurationMilliseconds: effectiveDuration), cancellationToken);
    }

    /// <summary>Clears LiveUI highlights without opening a Navigator tour.</summary>
    public ValueTask<LiveUIActionReceipt> ClearHighlightAsync(CancellationToken cancellationToken = default) =>
        ExecuteOnCurrentRouteAsync(null, LiveUIActionType.ClearHighlight, new(), cancellationToken);

    private async ValueTask<LiveUIActionReceipt> ExecuteOnCurrentRouteAsync(
        string? targetId,
        LiveUIActionType type,
        LiveUIActionParameters parameters,
        CancellationToken cancellationToken,
        bool resolveActivate = false)
    {
        await _actionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (context, manifest, route) = await GetCurrentRouteAsync(cancellationToken).ConfigureAwait(false);
            if (context is null || manifest is null || route is null)
                return Rejected("session_context_unavailable");
            if (type == LiveUIActionType.Navigate && parameters.DestinationRouteId is { } destinationId &&
                destinationId.StartsWith(LiveUIRuntimeRouteIds.Prefix, StringComparison.Ordinal) &&
                (runtimeDiscovery is null || !runtimeDiscovery.IsCurrentRoute(context, manifest, destinationId)))
                return new LiveUIActionReceipt(Guid.NewGuid().ToString("N"), context.SessionId,
                    LiveUIActionOutcome.Conflict, "runtime_route_stale", false, DateTimeOffset.UtcNow);
            if (targetId is not null && targetId.StartsWith(LiveUIRuntimeTargetIds.Prefix, StringComparison.Ordinal) &&
                (runtimeDiscovery is null || !runtimeDiscovery.IsCurrentTarget(context, manifest, targetId)))
                return new LiveUIActionReceipt(Guid.NewGuid().ToString("N"), context.SessionId,
                    LiveUIActionOutcome.Conflict, "runtime_target_stale", false, DateTimeOffset.UtcNow);
            if (type != LiveUIActionType.ClearHighlight)
            {
                lock (_pageGate)
                    if (_observedPages.TryGetValue(context.SessionId, out var observed) &&
                        (observed.RouteId != context.CurrentRouteId ||
                         !string.Equals(observed.Path, context.CurrentPath, StringComparison.Ordinal)))
                        return new LiveUIActionReceipt(Guid.NewGuid().ToString("N"), context.SessionId,
                            LiveUIActionOutcome.Conflict, "page_context_stale", false, DateTimeOffset.UtcNow);
            }
            if (resolveActivate && route.Targets.SingleOrDefault(target => target.Id == targetId)?.Kind == LiveUITargetKind.Tab)
            {
                // SelectTab has its own verification semantics and cannot honor an arbitrary state request.
                if (parameters.StateName is not null)
                    return Rejected("operation_unsupported");
                type = LiveUIActionType.SelectTab;
            }
            var action = CreateAction(context, manifest, route, targetId, type, parameters);
            var receipt = await orchestrator.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            if (type == LiveUIActionType.Navigate && receipt.Outcome == LiveUIActionOutcome.Completed && receipt.PostconditionVerified)
            {
                var destination = await contextAccessor.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
                if (destination is not null && destination.SessionId == context.SessionId &&
                    destination.CurrentRouteId == parameters.DestinationRouteId && destination.CurrentPath is not null)
                {
                    lock (_pageGate)
                        if (_observedPages.ContainsKey(context.SessionId) || _observedPages.Count < 256)
                            _observedPages[context.SessionId] = (destination.CurrentRouteId, destination.CurrentPath);
                }
            }
            return receipt;
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
            if (manifestValidator.Validate(manifest).Count != 0)
                return (context, null, null);
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
            route.Id, targetId, type, parameters, options.Value.Interaction.DefaultActionTimeout)
        { ExpectedPath = context.CurrentPath };

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
        actionType == LiveUIActionType.Select && target.Kind == LiveUITargetKind.Checkbox
            ? new(Value: "true")
            : actionType == LiveUIActionType.SetValue
            ? new(Value: string.Empty)
            : actionType == LiveUIActionType.WaitForState && (target.Kind is LiveUITargetKind.List or LiveUITargetKind.Table or LiveUITargetKind.Region)
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
    bool RequiresConfirmation)
{
    /// <summary>The label may be inferred from nearby page content rather than a semantic control label.</summary>
    public bool LabelMayBeInferred { get; init; }

    /// <summary>Authorized parent context for disambiguating similarly named controls.</summary>
    public string? ParentLabel { get; init; }
}
