using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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
    private const int MaxRenderedMessages = 4;
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
    private bool _voiceActive;
    private bool _sessionCreationStarted;
    private int _spokenResponseLength;
    private int _inputRenderKey;

    /// <summary>Optional absolute voice WebSocket endpoint. Defaults to the current origin gateway route.</summary>
    [Parameter] public string? VoiceEndpoint { get; set; }

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

    private bool CanType => true;

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

    private static MarkupString RenderMarkdown(string? source)
    {
        var encoded = WebUtility.HtmlEncode(source ?? string.Empty);
        var lines = encoded.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new StringBuilder();
        var inList = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                if (!inList) { output.Append("<ul>"); inList = true; }
                output.Append("<li>").Append(RenderInline(line[2..])).Append("</li>");
                continue;
            }

            if (inList) { output.Append("</ul>"); inList = false; }
            if (line.Length == 0) { output.Append("<br />"); continue; }
            if (line.StartsWith("### ", StringComparison.Ordinal))
                output.Append("<h5>").Append(RenderInline(line[4..])).Append("</h5>");
            else if (line.StartsWith("## ", StringComparison.Ordinal))
                output.Append("<h4>").Append(RenderInline(line[3..])).Append("</h4>");
            else if (line.StartsWith("# ", StringComparison.Ordinal))
                output.Append("<h3>").Append(RenderInline(line[2..])).Append("</h3>");
            else
                output.Append("<span>").Append(RenderInline(line)).Append("</span><br />");
        }

        if (inList) output.Append("</ul>");
        return new MarkupString(output.ToString());
    }

    private static string RenderInline(string value)
    {
        value = Regex.Replace(value, @"\[([^\]]+)\]\((https?://[^\s)]+)\)", "<a href=\"$2\" target=\"_blank\" rel=\"noopener noreferrer\">$1</a>", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"`([^`]+)`", "<code>$1</code>", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"\*\*([^*]+)\*\*", "<strong>$1</strong>", RegexOptions.CultureInvariant);
        value = Regex.Replace(value, @"(?<!\*)\*([^*]+)\*(?!\*)", "<em>$1</em>", RegexOptions.CultureInvariant);
        return value;
    }

    protected override void OnInitialized()
    {
        Session!.Changed += OnSessionChanged;
        if (StartOpen)
        {
            _open = true;
            _openedOnce = true;
        }
    }

    private void OnSessionChanged()
    {
        var response = Session?.StreamingText ?? string.Empty;
        if (_speechOn && response.Length > _spokenResponseLength)
        {
            var delta = response[_spokenResponseLength..];
            _spokenResponseLength = response.Length;
            _ = SpeakResponseDeltaAsync(delta);
        }
        else if (response.Length == 0 && Session?.Connection != ComposerConnectionState.Streaming)
        {
            _spokenResponseLength = 0;
        }

        _ = InvokeAsync(StateHasChanged);
    }

    private async Task SpeakResponseDeltaAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { await JS.InvokeVoidAsync("ocComposerSpeech.speak", text).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException) { }
    }

    private async Task ToggleSpeechAsync()
    {
        _speechOn = !_speechOn;
        if (!_speechOn)
        {
            try { await JS.InvokeVoidAsync("ocComposerSpeech.stop").ConfigureAwait(false); } catch (Exception) { }
        }
        else if (!string.IsNullOrWhiteSpace(Session?.StreamingText))
        {
            _spokenResponseLength = 0;
            OnSessionChanged();
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ToggleMicrophoneAsync()
    {
        _micOn = !_micOn;
        if (!_micOn && _voiceActive)
        {
            _voiceActive = false;
            try { await JS.InvokeVoidAsync("ocComposerVoice.stop").ConfigureAwait(false); } catch (Exception) { }
        }
        await InvokeAsync(StateHasChanged);
    }

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
        _inputRenderKey++;
        await InvokeAsync(StateHasChanged).ConfigureAwait(true);
        await ClearInputAsync().ConfigureAwait(true);
        await AutosizeAsync().ConfigureAwait(false);

        try
        {
            if (string.IsNullOrWhiteSpace(Session.SessionId))
            {
                try
                {
                    var createdSession = await Client.CreateSessionAsync(token).ConfigureAwait(false);
                    Session.AttachSession(createdSession.Id.Value.ToString());
                }
                catch (OpenConnectClientException ex) when (ex.StatusCodeValue is 401 or 403 or 404)
                {
                    // External gateways may require an independently authenticated session.
                    // Preserve the existing null-session task fallback in that mode.
                }
            }

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

    [JSInvokable]
    public async Task OnVoicePartial(string text, long speechEpoch)
    {
        if (Session.UpdateLiveHypothesis(text, speechEpoch))
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    [JSInvokable]
    public async Task OnVoiceFinal(string text, long speechEpoch)
    {
        if (!Session.CommitLiveTranscript(text, speechEpoch)) return;
        _voiceActive = false;
        _draft = string.Empty;
        _inputRenderKey++;
        await AutosizeAsync().ConfigureAwait(false);
        await InvokeAsync(StateHasChanged).ConfigureAwait(false);
    }

    [JSInvokable]
    public Task OnVoiceError(string message)
    {
        _voiceActive = false;
        Session.MarkError(string.IsNullOrWhiteSpace(message) ? "Voice input is unavailable. Text input remains available." : message);
        return InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public Task OnVoiceStopped()
    {
        _voiceActive = false;
        return InvokeAsync(StateHasChanged);
    }

    private async Task ToggleVoiceAsync()
    {
        if (!_micOn)
        {
            Session.MarkError("Microphone is turned off. Turn it on before starting voice input.");
            return;
        }

        if (_voiceActive)
        {
            _voiceActive = false;
            try { await JS.InvokeVoidAsync("ocComposerVoice.stop").ConfigureAwait(false); } catch (Exception) { }
            return;
        }

        if (string.IsNullOrWhiteSpace(Session.SessionId))
        {
            try
            {
                var created = await Client.CreateSessionAsync().ConfigureAwait(false);
                Session.AttachSession(created.Id.Value.ToString());
            }
            catch (Exception ex)
            {
                Session.MarkError(FriendlyError(ex));
                return;
            }
        }

        var epoch = Session.StartLiveListening(permissionGranted: true);
        if (Session.MicrophoneState == ComposerMicrophoneState.Failed) return;
        try
        {
            await JS.InvokeVoidAsync("ocComposerVoice.start", _dotNetRef, Session.SessionId, epoch, VoiceEndpoint).ConfigureAwait(false);
            _voiceActive = true;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException)
        {
            Session.MarkError("Microphone access or voice transcription is unavailable. Text input remains available.");
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

    private async Task EnsureSessionAsync()
    {
        if (_sessionCreationStarted || !string.IsNullOrWhiteSpace(Session.SessionId)) return;
        _sessionCreationStarted = true;
        try
        {
            var created = await Client.CreateSessionAsync().ConfigureAwait(false);
            await InvokeAsync(() => Session.AttachSession(created.Id.Value.ToString()));
        }
        catch (Exception ex)
        {
            await InvokeAsync(() => Session.MarkError(FriendlyError(ex)));
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Server prerendering has no JS interop: only attach once interactive.
        if ((_open || IsAttached) && !_disposed)
        {
            await EnsureSessionAsync().ConfigureAwait(false);
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

    private async Task ClearInputAsync()
    {
        try
        {
            await JS.InvokeVoidAsync("ocComposerInput.clear", _panelRef).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException)
        {
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
