using Microsoft.Extensions.Options;
using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

public sealed class LiveUIInferenceRouter(
    IEnumerable<ILiveUIInferenceProvider> providers,
    LiveUIActionPlanValidator validator,
    IOptions<LiveUIInferenceOptions> options) : ILiveUIInferenceRouter
{
    private readonly IReadOnlyDictionary<LiveUIInferenceProviderKind, ILiveUIInferenceProvider> _providers =
        providers.ToDictionary(provider => provider.Kind);

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Planning.Timeout);
        var order = settings.Mode switch
        {
            LiveUIInferenceMode.GatewayOnly => [LiveUIInferenceProviderKind.Gateway],
            LiveUIInferenceMode.GatewayFirst => new[] { LiveUIInferenceProviderKind.Gateway, LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceProviderKind.Browser },
            LiveUIInferenceMode.BrowserOnly => [LiveUIInferenceProviderKind.Browser],
            _ => new[] { LiveUIInferenceProviderKind.Browser, LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceProviderKind.Gateway }
        };
        Exception? lastFailure = null;
        foreach (var kind in order)
        {
            if (kind != order[0] && settings.Gateway.FallbackPolicy == LiveUIFallbackPolicy.Disabled) continue;
            if (kind == LiveUIInferenceProviderKind.Browser && !settings.Browser.Enabled) continue;
            if (kind == LiveUIInferenceProviderKind.LocalWorker && !settings.Worker.Enabled) continue;
            if (kind == LiveUIInferenceProviderKind.Gateway && (!settings.Gateway.Enabled || settings.Mode == LiveUIInferenceMode.BrowserOnly)) continue;
            if (!_providers.TryGetValue(kind, out var provider)) continue;
            try
            {
                var availability = await provider.GetAvailabilityAsync(timeout.Token).ConfigureAwait(false);
                if (availability.Status != LiveUIInferenceAvailabilityStatus.Available)
                {
                    lastFailure = new InvalidOperationException("inference_unavailable");
                    if (settings.Gateway.FallbackPolicy is LiveUIFallbackPolicy.OnFailure or LiveUIFallbackPolicy.OnUnavailable) continue;
                    continue;
                }
                var result = await provider.GeneratePlanAsync(request, timeout.Token).ConfigureAwait(false);
                if (result.Plan is null || !validator.Validate(request, result.Plan, settings.Planning.MaxActions, out _))
                {
                    lastFailure = new InvalidOperationException("invalid_plan");
                    if (settings.Gateway.FallbackPolicy == LiveUIFallbackPolicy.OnFailure) continue;
                    break;
                }
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { throw new TimeoutException("LiveUI inference timed out."); }
            catch (PrivacyBlockedException) { throw; }
            catch (UnauthorizedAccessException) { throw; }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lastFailure = exception;
                if (settings.Gateway.FallbackPolicy == LiveUIFallbackPolicy.OnFailure) continue;
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new TimeoutException("LiveUI inference timed out.");
        return new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, null,
            lastFailure?.Message == "invalid_plan" ? "invalid_plan" : "inference_unavailable");
    }
}

public sealed class LiveUIActionPlanValidator
{
    private static readonly HashSet<string> AllowedActions = ["activate", "navigate", "setValue", "select", "highlight", "focus", "scroll"];

    public bool Validate(LiveUIInferenceRequest request, LiveUIActionPlan plan, int maximumActions, out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != request.SchemaVersion) { reason = "schema_version_mismatch"; return false; }
        if (plan.Actions.Count == 0 || plan.Actions.Count > maximumActions) { reason = "action_count_invalid"; return false; }
        var targets = request.Capabilities.ToDictionary(capability => capability.TargetId, StringComparer.Ordinal);
        foreach (var action in plan.Actions)
        {
            if (!AllowedActions.Contains(action.Type) || !targets.TryGetValue(action.TargetId, out var target) || !target.Actions.Contains(action.Type, StringComparer.Ordinal))
            { reason = "capability_not_allowed"; return false; }
            if (action.Type == "setValue" && (action.Value is null || action.Value.Length > 256))
            { reason = "parameter_invalid"; return false; }
            if (action.Type != "setValue" && action.Value is not null)
            { reason = "parameter_invalid"; return false; }
        }
        reason = "valid";
        return true;
    }
}
