using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;
using System.Diagnostics;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Blazor interop provider for browser-managed Prompt API models.</summary>
public sealed class BrowserPromptApiInferenceProvider(IJSRuntime jsRuntime, IOptions<LiveUIInferenceOptions> options) : ILiveUIInferenceProvider, IAsyncDisposable
{
    private const string ModulePath = "./_content/OpenConnect.LiveUI.Blazor/liveui-inference.js";
    private const int CompletedCorrelationLimit = 1024;
    private static readonly TimeSpan CompletedCorrelationLifetime = TimeSpan.FromMinutes(10);
    private IJSObjectReference? _module;
    private bool _disposed;
    private readonly object _completedCorrelationsLock = new();
    private readonly Dictionary<string, long> _completedCorrelations = new(StringComparer.Ordinal);
    private readonly Queue<(string RequestId, long CompletedAt)> _completedCorrelationOrder = new();
    public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Browser;

    /// <summary>Initializes a browser model from a user gesture. This is the only API that may start a model download.</summary>
    public async Task<LiveUIInferenceAvailability> InitializeFromUserGestureAsync(CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<LiveUIInferenceAvailability>("initialize", cancellationToken,
            options.Value.Browser.Provider.ToString(), options.Value.Browser.EnableStructuredOutput).ConfigureAwait(false);
    }

    public async Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
            return await module.InvokeAsync<LiveUIInferenceAvailability>("getAvailability", cancellationToken,
                options.Value.Browser.Provider.ToString()).ConfigureAwait(false);
        }
        catch (JSDisconnectedException)
        {
            return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "browser_disconnected");
        }
        catch (JSException)
        {
            return new(Kind, LiveUIInferenceAvailabilityStatus.Unavailable, Reason: "browser_prompt_api_unavailable");
        }
    }

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
            var availability = await module.InvokeAsync<LiveUIInferenceAvailability>("initialize", cancellationToken,
                options.Value.Browser.Provider.ToString(), options.Value.Browser.EnableStructuredOutput).ConfigureAwait(false);
            if (availability.Status != LiveUIInferenceAvailabilityStatus.Available)
                return new(Kind, null, availability.Reason ?? availability.Status.ToString(), availability.Browser);
            var browserRequest = new LiveUIBrowserInferenceRequest(
                request.RequestId,
                request.ExecutionId,
                request.SessionId,
                request.SchemaVersion,
                request.ManifestHash,
                request.Intent,
                request.RouteId,
                request.Capabilities);
            var response = await module.InvokeAsync<LiveUIBrowserInferenceResponse>("generate", cancellationToken,
                browserRequest, options.Value.Browser.EnableStructuredOutput).ConfigureAwait(false);
            if (response is null ||
                !string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
                !string.Equals(response.ExecutionId, request.ExecutionId, StringComparison.Ordinal) ||
                !string.Equals(response.SessionId, request.SessionId, StringComparison.Ordinal) ||
                !string.Equals(response.SchemaVersion, request.SchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(response.ManifestHash, request.ManifestHash, StringComparison.Ordinal) ||
                response.Plan is null)
                return new(Kind, null, "browser_response_mismatch");

            var completionStatus = TryMarkCorrelationCompleted(request.RequestId);
            if (completionStatus == CorrelationCompletionStatus.Replay)
                return new(Kind, null, "browser_response_replay");
            if (completionStatus == CorrelationCompletionStatus.CapacityExceeded)
                return new(Kind, null, "browser_response_replay_cache_full");

            return new(Kind, response.Plan, "completed");
        }
        catch (OperationCanceledException)
        {
            if (_module is not null)
            {
                try { await _module.InvokeVoidAsync("cancel").ConfigureAwait(false); }
                catch (JSDisconnectedException) { }
                catch (JSException) { }
            }
            throw;
        }
        catch (JSDisconnectedException)
        {
            return new(Kind, null, "browser_disconnected");
        }
        catch (JSException)
        {
            return new(Kind, null, "browser_prompt_api_unavailable");
        }
    }

    private CorrelationCompletionStatus TryMarkCorrelationCompleted(string requestId)
    {
        lock (_completedCorrelationsLock)
        {
            var now = Stopwatch.GetTimestamp();
            PruneCompletedCorrelations(now);
            if (_completedCorrelations.ContainsKey(requestId))
                return CorrelationCompletionStatus.Replay;

            if (_completedCorrelations.Count >= CompletedCorrelationLimit)
                return CorrelationCompletionStatus.CapacityExceeded;

            _completedCorrelations.Add(requestId, now);
            _completedCorrelationOrder.Enqueue((requestId, now));
            return CorrelationCompletionStatus.Completed;
        }
    }

    private enum CorrelationCompletionStatus
    {
        Completed,
        Replay,
        CapacityExceeded
    }

    private void PruneCompletedCorrelations(long now)
    {
        var lifetimeTicks = (long)(CompletedCorrelationLifetime.TotalSeconds * Stopwatch.Frequency);
        while (_completedCorrelationOrder.TryPeek(out var entry) && now - entry.CompletedAt >= lifetimeTicks)
            RemoveOldestCompletedCorrelation();
    }

    private void RemoveOldestCompletedCorrelation()
    {
        var entry = _completedCorrelationOrder.Dequeue();
        if (_completedCorrelations.TryGetValue(entry.RequestId, out var completedAt) &&
            completedAt == entry.CompletedAt)
            _completedCorrelations.Remove(entry.RequestId);
    }

    private ValueTask<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _module is null
            ? ImportModuleAsync(cancellationToken)
            : ValueTask.FromResult(_module);
    }

    private async ValueTask<IJSObjectReference> ImportModuleAsync(CancellationToken cancellationToken)
    {
        _module = await jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, ModulePath).ConfigureAwait(false);
        return _module;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_module is null) return;
        try { await _module.InvokeVoidAsync("dispose").ConfigureAwait(false); }
        catch (JSDisconnectedException) { }
        try { await _module.DisposeAsync().ConfigureAwait(false); }
        catch (JSDisconnectedException) { }
    }
}

public sealed record LiveUIBrowserInferenceRequest(
    string RequestId,
    string ExecutionId,
    string SessionId,
    string SchemaVersion,
    string ManifestHash,
    string Intent,
    string RouteId,
    IReadOnlyList<LiveUIInferenceCapability> Capabilities);

public sealed record LiveUIBrowserInferenceResponse(
    string RequestId,
    string ExecutionId,
    string SessionId,
    string SchemaVersion,
    string ManifestHash,
    LiveUIActionPlan Plan);
