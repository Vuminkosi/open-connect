using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using OpenConnect.Client;
using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Anchored floating-assistant composer (text-only, real gateway).
/// Closed renders the FAB only; open renders a fixed bubble anchored above the
/// FAB with a tail so host layout never reflows. <see cref="ComposerPlacement.Attached"/>
/// is the attached mode: inline bubble with no FAB, tail, or drag handle.
/// </summary>
public partial class MiniComposer : ComponentBase, IAsyncDisposable
{
    private const int MaxRenderedMessages = 30;
    private const int MaxInputLengthHardCap = 8000;

    private bool _open;
    private bool _openedOnce;
    private bool _showSettings;
    private bool _micOn = true;
    private bool _speechOn = true;
    private bool _sending;
    private bool _disposed;
    private bool _dragAttached;
    private bool _enterAttached;
    private string _draft = string.Empty;
    private string _editorId = $"oc-editor-{Guid.NewGuid():N}";
    private string? _pendingIdempotencyKey;
    private CancellationTokenSource? _sendCts;
    private ElementReference _panelRef;
    private ElementReference _handleRef;
    private DotNetObjectReference<MiniComposer>? _dotNetRef;

    [Inject] private IOpenConnectClient Client { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;

    [CascadingParameter] private ComposerSession Session { get; set; } = default!;

    [CascadingParameter(Name = "OpenConnectProvider")] private OpenConnectProvider? Provider { get; set; }

    /// <summary>
    /// BottomRight (default) renders the anchored FAB + bubble. Attached renders
    /// the bubble in place with no FAB, tail, or drag handle. Other placements
    /// behave as floating. Local mapping: contract value Attached = attached mode.
    /// </summary>
    [Parameter] public ComposerPlacement Placement { get; set; } = ComposerPlacement.BottomRight;

    [Parameter] public ComposerCloseBehavior CloseBehavior { get; set; } = ComposerCloseBehavior.MinimizeOnly;

    [Parameter] public string AssistantName { get; set; } = "Assistant";

    [Parameter] public string Placeholder { get; set; } = "Ask…";

    /// <summary>Optional suggestion chips shown above the input.</summary>
    [Parameter] public IReadOnlyList<string>? Suggestions { get; set; }

    /// <summary>Maximum input characters; clamped to the 8000 contract cap.</summary>
    [Parameter] public int MaxInputLength { get; set; } = 8000;

    /// <summary>Whether the bubble starts open. Closed (FAB only) is the default.</summary>
    [Parameter] public bool StartOpen { get; set; }

    /// <summary>Icon displayed on the floating activator button. Defaults to the agent icon (<see cref="Icons.Material.Filled.SmartToy"/>).</summary>
    [Parameter] public string FabIcon { get; set; } = Icons.Material.Filled.SmartToy;

    /// <summary>Optional custom CSS class for the floating FAB.</summary>
    [Parameter] public string? FabClass { get; set; }

    /// <summary>Optional custom CSS style for the floating FAB.</summary>
    [Parameter] public string? FabStyle { get; set; }

    private bool IsAttached => Placement == ComposerPlacement.Attached;

    private bool ShowFab => !IsAttached;

    private int EffectiveMaxLength => Math.Min(Math.Max(MaxInputLength, 1), MaxInputLengthHardCap);

    private bool IsLoading => Session is not null
        && (Session.Connection == ComposerConnectionState.Streaming
            || Session.Connection == ComposerConnectionState.Connecting);

    private bool CanType => !IsLoading;

    private bool CanSend => !IsLoading && !_sending && !string.IsNullOrWhiteSpace(_draft);

    private IReadOnlyList<ComposerMessage> VisibleMessages
    {
        get
        {
            var all = Session?.Messages ?? Array.Empty<ComposerMessage>();
            return all.Count <= MaxRenderedMessages ? all : all.Skip(all.Count - MaxRenderedMessages).ToList();
        }
    }

    private string? SentMessage => VisibleMessages.LastOrDefault(m => m.Role == "user")?.Text;

    private string? ResponseText => !string.IsNullOrEmpty(Session?.StreamingText)
        ? Session.StreamingText
        : VisibleMessages.LastOrDefault(m => m.Role == "assistant")?.Text;

    private string? LivePartialTranscript => Session?.LivePartialTranscript;

    private string MotionClass => StatusText == "Thinking…" ? "oc-status__motion--thinking" :
        StatusText?.Contains("connect", StringComparison.OrdinalIgnoreCase) == true ? "oc-status__motion--connecting" :
        "oc-status__motion--pulse";

    private string StatusText => Session switch
    {
        null => "Not connected",
        { Connection: ComposerConnectionState.Streaming } => "Replying…",
        { Connection: ComposerConnectionState.Connecting } => "Connecting…",
        { Connection: ComposerConnectionState.Reconnecting } => "Reconnecting…",
        { Connection: ComposerConnectionState.Error } => "Connection issue",
        _ => Session.SessionId is null ? "No session" : "Ready",
    };

    private string CloseLabel => CloseBehavior switch
    {
        ComposerCloseBehavior.MinimizeOnly => "Minimize assistant",
        ComposerCloseBehavior.CancelActiveTask => "Stop reply and close",
        ComposerCloseBehavior.EndSession => "End session and close",
        _ => $"Close {AssistantName}",
    };

    protected override void OnInitialized()
    {
        Session!.Changed += OnSessionChanged;
        if (StartOpen)
        {
            _open = true;
            _openedOnce = true;
        }
    }

    private void OnSessionChanged() => _ = InvokeAsync(StateHasChanged);

    private void SelectMode(string mode)
    {
        _open = true;
        _openedOnce = true;
        if (mode is not "Ask")
        {
            _draft = $"[{mode}] ";
        }
    }

    private Task OnInputChanged(string? value)
    {
        _draft = value ?? string.Empty;
        StateHasChanged();
        return Task.CompletedTask;
    }

    [JSInvokable]
    public Task OnInteractPositionChanged(double x, double y)
    {
        _ = x;
        _ = y;
        return Task.CompletedTask;
    }

    [JSInvokable]
    public async Task SubmitKeyboardMessage(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > EffectiveMaxLength)
        {
            return;
        }

        _draft = value;
        if (CanSend)
        {
            await SendAsync();
        }
    }

    private async Task OnHandleKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Home")
        {
            try
            {
                await JS.InvokeVoidAsync("ocComposerDrag.detach", _handleRef, _panelRef, true).ConfigureAwait(false);
                await JS.InvokeVoidAsync("ocComposerDrag.attach", _handleRef, _panelRef, _dotNetRef).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException) { }
            return;
        }

        var step = e.Key switch
        {
            "ArrowUp" => (0.0, -24.0),
            "ArrowDown" => (0.0, 24.0),
            "ArrowLeft" => (-24.0, 0.0),
            "ArrowRight" => (24.0, 0.0),
            _ => ((double, double)?)null,
        };
        if (step is not null)
        {
            try
            {
                await JS.InvokeVoidAsync("ocComposerDrag.moveBy", _panelRef, step.Value.Item1, step.Value.Item2).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException) { }
        }
    }

    private void OpenMode(string mode)
    {
        _open = true;
        _openedOnce = true;
        StateHasChanged();
    }

    private void Toggle()
    {
        _open = !_open;
        _openedOnce = _openedOnce || _open;
    }

    private void Close()
    {
        if (CloseBehavior is ComposerCloseBehavior.CancelActiveTask or ComposerCloseBehavior.EndSession)
        {
            StopSending();
        }

        _open = false;
        _showSettings = false;
    }

    private Task UseSuggestion(string suggestion)
    {
        _draft = suggestion;
        return SendAsync();
    }

    private Task HandleInput(ChangeEventArgs args)
    {
        _ = args;
        return AutosizeAsync();
    }

    /// <summary>
    /// Managed fallback for Enter-to-send when the JS bridge is unavailable.
    /// Contract: the JS bridge (<c>composer-input.js</c>) owns plain-Enter. It
    /// ignores IME composition (isComposing / keyCode 229), held-key repeats,
    /// and modified keys. Blazor's <see cref="KeyboardEventArgs"/> exposes no
    /// composition state in .NET 8, so this fallback must never submit on
    /// plain Enter (it cannot tell IME confirm-Enter apart from send-Enter).
    /// It submits only on the explicit Ctrl+Enter / Meta+Enter shortcut
    /// without Shift/Alt and without repeat; everything else is left to the
    /// JS bridge (or to <see cref="SubmitFromJs"/>).
    /// </summary>
    private async Task HandleKeyDown(KeyboardEventArgs args)
    {
        if (args.Key is not ("Enter" or "NumpadEnter"))
        {
            return;
        }

        if (args.Repeat || args.ShiftKey || args.AltKey)
        {
            return;
        }

        await SendAsync().ConfigureAwait(false);
    }

    /// <summary>Invoked by the JS Enter bridge; carries the same idempotency guard.</summary>
    [JSInvokable]
    public Task SubmitFromJs() => SendAsync();

    private async Task SendAsync()
    {
        // Idempotency: collapse double-submits, held Enter, and overlapping sends.
        if (_sending || IsLoading)
        {
            return;
        }

        var text = _draft.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.Length > EffectiveMaxLength)
        {
            text = text[..EffectiveMaxLength];
        }

        _sending = true;
        _pendingIdempotencyKey = Guid.NewGuid().ToString("N");

        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;

        Session.BeginSend(text);
        _draft = string.Empty;
        await AutosizeAsync().ConfigureAwait(false);

        try
        {
            try
            {
                await Client.CreateTaskAsync(
                    new CreateTaskRequest(Guid.NewGuid().ToString("N"), _pendingIdempotencyKey, text, Session.SessionId),
                    token).ConfigureAwait(false);
            }
            catch (OpenConnectClientException ex) when (ex.StatusCodeValue == 404)
            {
                // Stale or uninitialized session on gateway: retry with null sessionId so gateway auto-creates the session
                await Client.CreateTaskAsync(
                    new CreateTaskRequest(Guid.NewGuid().ToString("N"), _pendingIdempotencyKey, text, null),
                    token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await InvokeAsync(Session.CancelStreaming).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await InvokeAsync(() => Session.MarkError(FriendlyError(ex))).ConfigureAwait(true);
        }
        finally
        {
            _sending = false;
            _pendingIdempotencyKey = null;
            await InvokeAsync(StateHasChanged).ConfigureAwait(true);
        }
    }

    private void StopSending()
    {
        try
        {
            _sendCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        Session?.CancelStreaming();
    }

    private void Retry()
    {
        Session?.DismissError();
        Provider?.Reconnect();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Server prerendering has no JS interop: only attach once interactive.
        if ((_open || IsAttached) && !_disposed)
        {
            await AttachJsAsync().ConfigureAwait(false);
            await AutosizeAsync().ConfigureAwait(false);

            if (_open && _openedOnce)
            {
                try
                {
                    await JS.InvokeVoidAsync("ocComposerInput.focus", _panelRef).ConfigureAwait(false);
                }
                catch (Exception)
                {
                }
            }
        }
        else if (!_open && !IsAttached)
        {
            await DetachJsAsync(dragOnly: true).ConfigureAwait(false);
        }

        await base.OnAfterRenderAsync(firstRender).ConfigureAwait(false);
    }

    private async Task AttachJsAsync()
    {
        try
        {
            if (!_dragAttached && !IsAttached)
            {
                _dotNetRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ocComposerDrag.attach", _handleRef, _panelRef, _dotNetRef).ConfigureAwait(false);
                _dragAttached = true;
            }

            if (!_enterAttached)
            {
                _dotNetRef ??= DotNetObjectReference.Create(this);
                await JS.InvokeVoidAsync("ocComposerInput.attachEnter", _panelRef, _dotNetRef).ConfigureAwait(false);
                _enterAttached = true;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException)
        {
            // Prerendering or missing JS: managed fallback keeps text working.
        }
    }

    private async Task AutosizeAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("ocComposerInput.autosize", _panelRef).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException)
        {
        }
    }

    private async Task DetachJsAsync(bool dragOnly)
    {
        try
        {
            if (_dragAttached)
            {
                await JS.InvokeVoidAsync("ocComposerDrag.detach", _handleRef).ConfigureAwait(false);
                _dragAttached = false;
            }

            if (!dragOnly && _enterAttached)
            {
                await JS.InvokeVoidAsync("ocComposerInput.detach", _panelRef).ConfigureAwait(false);
                _enterAttached = false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException)
        {
        }
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        OpenConnectClientException clientEx => string.IsNullOrWhiteSpace(clientEx.Message)
            ? "The assistant could not complete the request. Try again."
            : clientEx.Message,
        HttpRequestException => "Could not reach the assistant service. Check your connection and retry.",
        _ => "Something went wrong sending your message. Try again.",
    };

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Session!.Changed -= OnSessionChanged;
        try
        {
            _sendCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        _sendCts?.Dispose();
        await DetachJsAsync(dragOnly: false).ConfigureAwait(false);
        _dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}
