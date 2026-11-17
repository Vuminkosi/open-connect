using System.Diagnostics;
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
        var startedAt = Stopwatch.GetTimestamp();
        var outcome = "failed";
        var metricProvider = "none";
        var failureRecorded = false;
        void RecordFailure(string errorType)
        {
            if (failureRecorded) return;
            LiveUIInferenceMetrics.Failures.Add(1,
                new KeyValuePair<string, object?>("provider", metricProvider),
                new KeyValuePair<string, object?>("error.type", errorType));
            failureRecorded = true;
        }

        try
        {
            var result = await GeneratePlanCoreAsync(request, cancellationToken,
                provider => metricProvider = LiveUIInferenceMetrics.Provider(provider)).ConfigureAwait(false);
            outcome = result.Plan is not null
                ? "completed"
                : result.Outcome is "invalid_plan" or "rate_limited" ? result.Outcome : "inference_unavailable";
            if (result.Plan is null) RecordFailure(outcome);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (TimeoutException)
        {
            outcome = "timeout";
            RecordFailure("timeout");
            throw;
        }
        catch (PrivacyBlockedException)
        {
            outcome = "privacy_blocked";
            RecordFailure("privacy_blocked");
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            outcome = "authorization_denied";
            RecordFailure("authorization_denied");
            throw;
        }
        catch (Exception exception)
        {
            outcome = "failed";
            RecordFailure(ErrorType(exception));
            throw;
        }
        finally
        {
            LiveUIInferenceMetrics.Requests.Add(1,
                new KeyValuePair<string, object?>("provider", metricProvider),
                new KeyValuePair<string, object?>("outcome", outcome));
            LiveUIInferenceMetrics.Duration.Record(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", metricProvider),
                new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private async Task<LiveUIInferenceResult> GeneratePlanCoreAsync(
        LiveUIInferenceRequest request,
        CancellationToken cancellationToken,
        Action<LiveUIInferenceProviderKind> providerSelected)
    {
        var settings = options.Value;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(settings.Planning.Timeout);
        LiveUIInferenceProviderKind[] order = settings.Mode switch
        {
            LiveUIInferenceMode.GatewayOnly => [LiveUIInferenceProviderKind.Gateway],
            LiveUIInferenceMode.GatewayFirst or LiveUIInferenceMode.Automatic =>
                [LiveUIInferenceProviderKind.Gateway, LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceProviderKind.Browser],
            LiveUIInferenceMode.BrowserOnly => [LiveUIInferenceProviderKind.Browser],
            LiveUIInferenceMode.BrowserFirst =>
                [LiveUIInferenceProviderKind.Browser, LiveUIInferenceProviderKind.LocalWorker, LiveUIInferenceProviderKind.Gateway],
            _ => [LiveUIInferenceProviderKind.Gateway]
        };
        Exception? lastFailure = null;
        var lastErrorType = "inference_unavailable";
        foreach (var kind in order)
        {
            // Gateway fallback is independently controlled. An enabled local worker remains
            // a permitted privacy-local fallback even when external inference is disabled.
            if (kind == LiveUIInferenceProviderKind.Gateway &&
                kind != order[0] &&
                settings.Gateway.FallbackPolicy == LiveUIFallbackPolicy.Disabled) continue;
            if (kind == LiveUIInferenceProviderKind.Browser && !settings.Browser.Enabled) continue;
            if (kind == LiveUIInferenceProviderKind.LocalWorker && !settings.Worker.Enabled) continue;
            if (kind == LiveUIInferenceProviderKind.Gateway && (!settings.Gateway.Enabled || settings.Mode == LiveUIInferenceMode.BrowserOnly)) continue;
            if (!_providers.TryGetValue(kind, out var provider)) continue;
            providerSelected(kind);
            var providerTag = LiveUIInferenceMetrics.Provider(kind);
            if (kind != order[0])
            {
                LiveUIInferenceMetrics.Fallbacks.Add(1,
                    new KeyValuePair<string, object?>("provider", providerTag),
                    new KeyValuePair<string, object?>("reason", lastErrorType));
            }
            try
            {
                var availability = await provider.GetAvailabilityAsync(timeout.Token).ConfigureAwait(false);
                LiveUIInferenceMetrics.ModelAvailability.Add(1,
                    new KeyValuePair<string, object?>("provider", providerTag),
                    new KeyValuePair<string, object?>("status", AvailabilityStatus(availability.Status)));
                if (availability.Status == LiveUIInferenceAvailabilityStatus.Blocked && availability.Reason == "rate_limited")
                    return new LiveUIInferenceResult(kind, null, "rate_limited");
                if (availability.Status != LiveUIInferenceAvailabilityStatus.Available)
                {
                    lastFailure = new InvalidOperationException("inference_unavailable");
                    lastErrorType = "inference_unavailable";
                    if (settings.Gateway.FallbackPolicy is LiveUIFallbackPolicy.OnFailure or LiveUIFallbackPolicy.OnUnavailable) continue;
                    continue;
                }
                var result = await provider.GeneratePlanAsync(request, timeout.Token).ConfigureAwait(false);
                if (result.Outcome is "browser_response_replay_cache_full" or "rate_limited")
                {
                    // Security boundaries and quotas must not be bypassed by fallback.
                    return result;
                }
                if (result.Plan is null && result.Outcome is "inference_unavailable" or "worker_unavailable" or
                    "gateway_unavailable" or "gateway_worker_unavailable" or "gateway_planner_unavailable" or "browser_disconnected" or
                    "browser_prompt_api_unavailable")
                {
                    lastFailure = new InvalidOperationException("inference_unavailable");
                    lastErrorType = "inference_unavailable";
                    continue;
                }
                if (result.Plan is null || !validator.Validate(request, result.Plan, settings.Planning.MaxActions, out _))
                {
                    lastFailure = new InvalidOperationException("invalid_plan");
                    lastErrorType = "invalid_plan";
                    LiveUIInferenceMetrics.InvalidPlans.Add(1,
                        new KeyValuePair<string, object?>("provider", providerTag));
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
                lastErrorType = ErrorType(exception);
                if (settings.Gateway.FallbackPolicy == LiveUIFallbackPolicy.OnFailure) continue;
                break;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new TimeoutException("LiveUI inference timed out.");
        return new LiveUIInferenceResult(LiveUIInferenceProviderKind.Browser, null,
            lastFailure?.Message == "invalid_plan" ? "invalid_plan" : "inference_unavailable");
    }

    private static string AvailabilityStatus(LiveUIInferenceAvailabilityStatus status) => status switch
    {
        LiveUIInferenceAvailabilityStatus.Available => "available",
        LiveUIInferenceAvailabilityStatus.Downloadable => "downloadable",
        LiveUIInferenceAvailabilityStatus.Downloading => "downloading",
        LiveUIInferenceAvailabilityStatus.Blocked => "blocked",
        _ => "unavailable"
    };

    private static string ErrorType(Exception exception) => exception switch
    {
        TimeoutException => "timeout",
        PrivacyBlockedException => "privacy_blocked",
        UnauthorizedAccessException => "authorization_denied",
        _ => "provider_failure"
    };
}

public sealed class LiveUIActionPlanValidator
{
    private static readonly HashSet<string> AllowedActions = ["activate", "navigate", "setValue", "select", "highlight", "focus", "scroll", "inspect"];

    public bool Validate(LiveUIInferenceRequest request, LiveUIActionPlan plan, int maximumActions, out string reason)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.SchemaVersion != request.SchemaVersion) { reason = "schema_version_mismatch"; return false; }
        if (plan.Actions is null || plan.Actions.Count == 0 || plan.Actions.Count > maximumActions) { reason = "action_count_invalid"; return false; }
        if (request.Capabilities is null) { reason = "invalid_capabilities"; return false; }
        var targets = new Dictionary<string, LiveUIInferenceCapability>(StringComparer.Ordinal);
        foreach (var capability in request.Capabilities)
        {
            if (capability is null || string.IsNullOrEmpty(capability.TargetId) || capability.Actions is null ||
                !targets.TryAdd(capability.TargetId, capability))
            { reason = "invalid_capabilities"; return false; }
        }
        foreach (var action in plan.Actions)
        {
            if (action is null || string.IsNullOrEmpty(action.Type) || string.IsNullOrEmpty(action.TargetId) ||
                !AllowedActions.Contains(action.Type) || !targets.TryGetValue(action.TargetId, out var target) || !target.Actions.Contains(action.Type, StringComparer.Ordinal))
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
