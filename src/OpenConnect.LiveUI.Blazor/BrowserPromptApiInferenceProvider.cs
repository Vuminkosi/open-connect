using Microsoft.JSInterop;
using Microsoft.Extensions.Options;
using OpenConnect.LiveUI.Inference;

namespace OpenConnect.LiveUI.Blazor;

/// <summary>Blazor interop provider for browser-managed Prompt API models.</summary>
public sealed class BrowserPromptApiInferenceProvider(IJSRuntime jsRuntime, IOptions<LiveUIInferenceOptions> options) : ILiveUIInferenceProvider, IAsyncDisposable
{
    private const string ModulePath = "./_content/OpenConnect.LiveUI.Blazor/liveui-inference.js";
    private IJSObjectReference? _module;
    private bool _disposed;
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
            var plan = await module.InvokeAsync<LiveUIActionPlan>("generate", cancellationToken,
                request, options.Value.Browser.EnableStructuredOutput);
            return new(Kind, plan, "completed");
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
