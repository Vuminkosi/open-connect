using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace OpenConnect.LiveUI;

public sealed class LiveUIOrchestrator(
    ILiveUIManifestProvider manifestProvider,
    ILiveUIActionAuthorizer authorizer,
    ILiveUIRuntime runtime,
    IOptions<LiveUIOptions> options,
    LiveUIManifestValidator manifestValidator)
{
    private readonly ConcurrentDictionary<(string SessionId, string ActionId), byte> _seenActions = new();

    public async ValueTask<LiveUIActionReceipt> ExecuteAsync(LiveUIAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var config = options.Value;
        if (!IsValidAction(action, config, out var validationCode))
            return Receipt(action, LiveUIActionOutcome.Failed, validationCode, false);

        if (!_seenActions.TryAdd((action.SessionId, action.ActionId), 0))
            return Receipt(action, LiveUIActionOutcome.Conflict, "duplicate_action", false);

        try
        {
            var manifest = await manifestProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var manifestErrors = manifestValidator.Validate(manifest);
            if (manifestErrors.Count > 0)
                return Receipt(action, LiveUIActionOutcome.Failed, manifestErrors[0], false);

            if (manifest!.Routes.Any(route => route.Targets.Count > config.Discovery.MaximumTargetsPerRoute))
                return Receipt(action, LiveUIActionOutcome.Failed, "manifest_target_limit", false);

            if (!string.Equals(action.ManifestHash, manifest.ContentHash, StringComparison.Ordinal))
                return Receipt(action, LiveUIActionOutcome.Conflict, "manifest_stale", false);

            if (action.Type == LiveUIActionType.Navigate &&
                !manifest.Routes.Any(candidate => candidate.Id == action.Parameters.DestinationRouteId))
                return Receipt(action, LiveUIActionOutcome.Failed, "route_unknown", false);

            if (action.Type == LiveUIActionType.Navigate && !config.Interaction.EnableNavigation)
                return Receipt(action, LiveUIActionOutcome.Denied, "navigation_disabled", false);
            if (action.Type is LiveUIActionType.Highlight or LiveUIActionType.ClearHighlight && !config.Interaction.EnableHighlighting)
                return Receipt(action, LiveUIActionOutcome.Denied, "highlighting_disabled", false);

            var route = manifest.Routes.SingleOrDefault(candidate => candidate.Id == action.RouteId);
            if (route is null) return Receipt(action, LiveUIActionOutcome.Failed, "route_unknown", false);

            var target = action.TargetId is null ? null : route.Targets.SingleOrDefault(candidate => candidate.Id == action.TargetId);
            if (action.TargetId is not null && target is null)
                return Receipt(action, LiveUIActionOutcome.MissingTarget, "target_unknown", false);

            var requiredCapability = GetRequiredCapability(action.Type);
            if (requiredCapability is not null && (target is null || !target.Capabilities.Contains(requiredCapability.Value)))
                return Receipt(action, LiveUIActionOutcome.Denied, "capability_unavailable", false);
            if (!config.Interaction.EnableFiltering && target?.Capabilities.Contains(LiveUICapability.Filter) == true)
                return Receipt(action, LiveUIActionOutcome.Denied, "filtering_disabled", false);

            var context = new LiveUIExecutionContext(manifest, route, target, action);
            var decision = await authorizer.AuthorizeAsync(context, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
                return Receipt(action, LiveUIActionOutcome.Denied,
                    string.IsNullOrWhiteSpace(decision.Code) ? "unauthorized" : decision.Code, false);
            if (config.Security.RequireConfirmationForWrites && target?.RequiresConfirmation == true && !decision.UserConfirmed)
                return Receipt(action, LiveUIActionOutcome.Denied, "approval_required", false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(action.Timeout);
            var result = await runtime.ExecuteAsync(context, timeout.Token).ConfigureAwait(false);
            if (result.Outcome == LiveUIActionOutcome.Completed && !result.PostconditionVerified)
                return Receipt(action, LiveUIActionOutcome.Uncertain, "postcondition_unverified", false);
            return Receipt(action, result.Outcome, result.Code, result.PostconditionVerified, result.VerifiedState);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Receipt(action, LiveUIActionOutcome.Uncertain, "action_timeout", false);
        }
        catch (OperationCanceledException)
        {
            return Receipt(action, LiveUIActionOutcome.Uncertain, "action_cancelled", false);
        }
        catch
        {
            return Receipt(action, LiveUIActionOutcome.Failed, "execution_failed", false);
        }
    }

    public int ClearSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var removed = 0;
        foreach (var key in _seenActions.Keys.Where(key => key.SessionId == sessionId))
        {
            if (_seenActions.TryRemove(key, out _)) removed++;
        }
        return removed;
    }

    private static bool IsValidAction(LiveUIAction action, LiveUIOptions config, out string code)
    {
        code = "invalid_action";
        if (string.IsNullOrWhiteSpace(action.ActionId) || action.ActionId.Length > config.Security.MaximumActionIdLength ||
            string.IsNullOrWhiteSpace(action.SessionId) || action.SessionId.Length > 128 ||
            string.IsNullOrWhiteSpace(action.ManifestHash) || action.ManifestHash.Length > 128 ||
            string.IsNullOrWhiteSpace(action.RouteId) || action.RouteId.Length > 256 ||
            action.TargetId is { Length: > 256 } || action.TargetId is { Length: 0 } ||
            action.SchemaVersion != LiveUISchema.CurrentVersion || !Enum.IsDefined(action.Type) ||
            action.Parameters is null || action.Timeout <= TimeSpan.Zero || action.Timeout > TimeSpan.FromMinutes(2))
            return false;
        if (action.Parameters.Value is { Length: > 10_000 } ||
            action.Parameters.DestinationRouteId is { Length: > 256 } ||
            action.Parameters.StateName is { Length: > 32 } ||
            action.Parameters.StateValue is { Length: > 2_000 } ||
            action.Parameters.ScrollAlignment is not (null or "start" or "center" or "end" or "nearest") ||
            action.Parameters.HighlightDurationMilliseconds is < 0 or > 30_000 ||
            action.Type != LiveUIActionType.Highlight && action.Parameters.HighlightDurationMilliseconds != 0)
            return false;
        if (action.Parameters.StateName is not null &&
            action.Parameters.StateName is not ("selected" or "checked" or "expanded" or "visible" or "exists" or "textContains" or "textNotContains"))
            return false;
        if (action.Type == LiveUIActionType.Navigate && string.IsNullOrWhiteSpace(action.Parameters.DestinationRouteId))
            return false;
        if (action.Type is LiveUIActionType.SetValue or LiveUIActionType.Select &&
            action.Parameters.Value is null)
            return false;
        code = string.Empty;
        return true;
    }

    private static LiveUICapability? GetRequiredCapability(LiveUIActionType type) => type switch
    {
        LiveUIActionType.Navigate or LiveUIActionType.ClearHighlight => null,
        LiveUIActionType.Open or LiveUIActionType.Click => LiveUICapability.Activate,
        LiveUIActionType.Close => LiveUICapability.Close,
        LiveUIActionType.Focus => LiveUICapability.Focus,
        LiveUIActionType.Scroll => LiveUICapability.Scroll,
        LiveUIActionType.Highlight => LiveUICapability.Highlight,
        LiveUIActionType.SetValue => LiveUICapability.SetValue,
        LiveUIActionType.Select => LiveUICapability.Select,
        LiveUIActionType.SelectTab => LiveUICapability.Activate,
        LiveUIActionType.Inspect => LiveUICapability.Inspect,
        LiveUIActionType.WaitForState => LiveUICapability.WaitForState,
        _ => null
    };

    private static LiveUIActionReceipt Receipt(LiveUIAction action, LiveUIActionOutcome outcome, string code,
        bool verified, string? state = null) =>
        new(action.ActionId, action.SessionId, outcome, code, verified, DateTimeOffset.UtcNow, state);
}
