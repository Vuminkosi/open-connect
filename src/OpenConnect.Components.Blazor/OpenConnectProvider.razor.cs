using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Client;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Cascades the shared <see cref="ComposerSession"/> (client state, session id,
/// event stream cursor) and the <see cref="OpenConnectComponentRegistry"/>
/// (component catalogue) to descendant components such as <see cref="MiniComposer"/>
/// and <see cref="OpenConnectNavigatorProvider"/>.
/// Consumes <see cref="IOpenConnectClient"/> over HTTP; never uses NATS in the browser.
/// </summary>
public partial class OpenConnectProvider : ComponentBase, IAsyncDisposable
{
    private CancellationTokenSource? _loopCts;
    private string? _subscribedSessionId;
    private bool _disposed;

    [Inject] private IOpenConnectClient Client { get; set; } = default!;

    [Inject] private OpenConnectComponentRegistry Registry { get; set; } = default!;

    [Inject] private ComposerSession Session { get; set; } = default!;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = default!;

    [Inject] private IEnumerable<IConfigureOpenConnectComponent> ComponentConfigurations { get; set; }
        = Enumerable.Empty<IConfigureOpenConnectComponent>();

    [CascadingParameter(Name = "OpenConnectProvider")]
    private OpenConnectProvider? ParentProvider { get; set; }

    /// <summary>Gateway session whose events are streamed. Required.</summary>
    [Parameter] public string? SessionId { get; set; }

    /// <summary>When true (default), the provider polls the event snapshot endpoint.</summary>
    [Parameter] public bool AutoSubscribe { get; set; } = true;

    /// <summary>Delay between snapshot polls. Defaults to 2 seconds.</summary>
    [Parameter] public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void OnInitialized()
    {
        if (ParentProvider is not null && !ReferenceEquals(ParentProvider, this))
        {
            throw new InvalidOperationException("Nested OpenConnectProvider instances are not permitted. Use a single root OpenConnectProvider.");
        }

        foreach (var configuration in ComponentConfigurations)
        {
            configuration.Apply(Registry);
        }

        Session.Changed += OnSessionChanged;
    }

    protected override void OnParametersSet()
    {
        var sessionId = SessionId?.Trim();
        if (!string.IsNullOrEmpty(sessionId) && !string.Equals(sessionId, Session.SessionId, StringComparison.Ordinal))
        {
            Session.AttachSession(sessionId);
        }

        if (AutoSubscribe && !string.IsNullOrEmpty(Session.SessionId))
        {
            RestartLoop(Session.SessionId!);
        }
        else if (!AutoSubscribe)
        {
            StopLoop();
        }
    }

    /// <summary>Forces a reconnect of the event stream (used by the composer's retry action).</summary>
    public void Reconnect()
    {
        var sessionId = Session.SessionId;
        if (!string.IsNullOrEmpty(sessionId))
        {
            RestartLoop(sessionId!, force: true);
        }
    }

    private void RestartLoop(string sessionId, bool force = false)
    {
        if (!force && string.Equals(_subscribedSessionId, sessionId, StringComparison.Ordinal) && _loopCts is not null)
        {
            return;
        }

        StopLoop();
        _subscribedSessionId = sessionId;
        _loopCts = new CancellationTokenSource();
        _ = RunLoopAsync(sessionId, _loopCts.Token);
    }

    private void StopLoop()
    {
        _subscribedSessionId = null;
        try
        {
            _loopCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        _loopCts?.Dispose();
        _loopCts = null;
    }

    private async Task RunLoopAsync(string sessionIdValue, CancellationToken token)
    {
        SessionId sessionId;
        try
        {
            sessionId = new SessionId(Ulid.Parse(sessionIdValue));
        }
        catch (FormatException)
        {
            Session.MarkError("The session reference is not valid.");
            return;
        }

        var clientOptions = ServiceProvider.GetService<OpenConnectClientOptions>();
        var reconnectEnabled = clientOptions?.Reconnect.Enabled ?? true;
        var resumeFromLastCursor = clientOptions?.Reconnect.ResumeFromLastCursor ?? true;

        Session.MarkConnecting();
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    var cursor = resumeFromLastCursor ? Session.LastSequence : 0;
                    await foreach (var record in Client.SubscribeEventsAsync(sessionId, cursor, token).ConfigureAwait(false))
                    {
                        Session.ApplyEvent(record);
                    }

                    failures = 0;
                    await InvokeAsync(StateHasChanged);
                    await Task.Delay(PollInterval, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    failures++;
                    if (!reconnectEnabled)
                    {
                        Session.MarkError("Connection interrupted and automatic reconnect is disabled.");
                        await InvokeAsync(StateHasChanged);
                        break;
                    }

                    Session.MarkReconnecting(failures == 1
                        ? "Connection interrupted. Retrying…"
                        : $"Connection interrupted (attempt {failures}). Retrying…");
                    await InvokeAsync(StateHasChanged);
                    var backoff = TimeSpan.FromSeconds(Math.Min(5 + (failures * 2), 30));
                    try
                    {
                        await Task.Delay(backoff, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    _ = ex;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void OnSessionChanged() => _ = InvokeAsync(StateHasChanged);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session.Changed -= OnSessionChanged;
        StopLoop();
        Session.DisposeVoiceState();
        try
        {
            ServiceProvider?.GetService<IVoiceClientService>()?.InvalidateAllEpochs();
        }
        catch (ObjectDisposedException)
        {
            // Service provider engine scope can already be disposed during circuit teardown.
        }
        await Task.CompletedTask;
        GC.SuppressFinalize(this);
    }
}
