using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using MudBlazor;
using OpenConnect.Abstractions;
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
    private const int MaxInputLengthHardCap = 8000;
    private static readonly MarkdownPipeline SpeechMarkdownPipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    private bool _open;
    private bool _openedOnce;
    private bool _micOn = false;
    private bool _speechOn = true;
    private bool _sending;
    private bool _disposed;
    private bool _dragAttached;
    private bool _enterAttached;
    private string _draft = string.Empty;
    private string _editorId = $"oc-editor-{Guid.NewGuid():N}";
    private string? _pendingIdempotencyKey;
    private string? _pendingTaskId;
    private string? _pendingSubmissionDraft;
    private string? _pendingSubmissionSessionId;
    private bool _pendingSubmissionDisplayed;
    private CancellationTokenSource? _sendCts;
    private TaskId? _activeTaskId;
    private bool _admissionInFlight;
    private bool _stopRequested;
    private readonly HashSet<TaskId> _cancellingTaskIds = [];
    private ElementReference _panelRef;
    private ElementReference _handleRef;
    private DotNetObjectReference<MiniComposer>? _dotNetRef;
    private bool _voiceActive;
    private Task? _sessionCreationTask;
    private bool _sessionCreationFailed;
    private int _inputRenderKey;
    private MudMenu? _settingsMenu;
    private int _speechCursor;
    private readonly Queue<SpeechSegment> _speechQueue = new();
    private CancellationTokenSource? _speechCts;
    private bool _speechWorkerRunning;
    private int _spokenResponseCharacters;
    private int _beingSpokenStart = -1;
    private int _beingSpokenEnd = -1;
    private string? _speechDisplayText;
    private string? _speechError;
    private string? _copiedResponse;
    private string? _lastNavigatorAction;
    private (string? SessionId, int MessageCount, long Sequence)? _lastSpeechResponseCompleted;

    private sealed record SpeechSegment(string Text, int StartCursor, int EndCursor);

    /// <summary>Optional absolute voice WebSocket endpoint. Defaults to the current origin gateway route.</summary>
    [Parameter] public string? VoiceEndpoint { get; set; }

    /// <summary>Optionally issues a single-use voice connection ticket for the current session.</summary>
    [Parameter] public Func<string, string?>? VoiceConnectionTicket { get; set; }

    [Inject] private IOpenConnectClient Client { get; set; } = default!;

    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private IDialogService DialogService { get; set; } = default!;

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

    /// <summary>Receives content-free playback lifecycle events from the client audio player.</summary>
    [Parameter] public EventCallback<ComposerSpeechPlaybackEvent> SpeechPlaybackChanged { get; set; }

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
            var exchanges = new List<List<ComposerMessage>>();
            List<ComposerMessage>? current = null;
            foreach (var message in all)
            {
                if (message.Role == "user")
                {
                    if (current is not null)
                        exchanges.Add(current);
                    current = new List<ComposerMessage>();
                }
                if (current is not null && (current.Count == 0 || current[^1].Role != message.Role || !string.Equals(current[^1].Text, message.Text, StringComparison.Ordinal)))
                    current.Add(message);
            }
            if (current is not null)
                exchanges.Add(current);
            return exchanges.TakeLast(2).SelectMany(exchange => exchange).ToList();
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
                if (!inList)
                { output.Append("<ul>"); inList = true; }
                output.Append("<li>").Append(RenderInline(line[2..])).Append("</li>");
                continue;
            }

            if (inList)
            { output.Append("</ul>"); inList = false; }
            if (line.Length == 0)
            { output.Append("<br />"); continue; }
            if (line.StartsWith("### ", StringComparison.Ordinal))
                output.Append("<h5>").Append(RenderInline(line[4..])).Append("</h5>");
            else if (line.StartsWith("## ", StringComparison.Ordinal))
                output.Append("<h4>").Append(RenderInline(line[3..])).Append("</h4>");
            else if (line.StartsWith("# ", StringComparison.Ordinal))
                output.Append("<h3>").Append(RenderInline(line[2..])).Append("</h3>");
            else
                output.Append("<span>").Append(RenderInline(line)).Append("</span><br />");
        }

        if (inList)
            output.Append("</ul>");
        return new MarkupString(output.ToString());
    }

    private string CopyAriaLabel(string text) => _copiedResponse == text ? "Response copied" : "Copy response to clipboard";
    private string CopyTitle(string text) => _copiedResponse == text ? "Copied" : "Copy response";

    private async Task CopyResponseAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        try
        {
            var copied = await JS.InvokeAsync<bool>("ocComposerInput.copy", text).ConfigureAwait(false);
            if (!copied)
                return;
            _copiedResponse = text;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            if (string.Equals(_copiedResponse, text, StringComparison.Ordinal))
            {
                _copiedResponse = null;
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JSException or JSDisconnectedException) { }
    }

    private bool ShouldRenderLiveResponse() =>
        !string.Equals(Session?.StreamingText, Session?.Messages.LastOrDefault(message => message.Role == "assistant")?.Text, StringComparison.Ordinal);

    private bool ShouldRenderSpeechProgress(ComposerMessage message) =>
        _speechOn && string.Equals(message.Text, _speechDisplayText, StringComparison.Ordinal) &&
        (_beingSpokenStart >= 0 || _spokenResponseCharacters < message.Text.Length);

    private MarkupString RenderSpeechProgress(string source)
    {
        var spoken = Math.Clamp(_spokenResponseCharacters, 0, source.Length);
        var activeStart = Math.Clamp(_beingSpokenStart, spoken, source.Length);
        var activeEnd = Math.Clamp(_beingSpokenEnd, activeStart, source.Length);
        var output = new StringBuilder();
        if (spoken > 0)
            output.Append("<span class=\"oc-speech-spoken\" style=\"color:var(--mud-palette-text-primary)\">").Append(RenderInline(WebUtility.HtmlEncode(source[..spoken]))).Append("</span>");
        if (activeEnd > activeStart)
            output.Append("<span class=\"oc-speech-speaking\" style=\"color:var(--mud-palette-primary);font-weight:600\">").Append(RenderInline(WebUtility.HtmlEncode(source[activeStart..activeEnd]))).Append("</span>");
        if (activeEnd < source.Length)
            output.Append("<span class=\"oc-speech-pending\" style=\"color:var(--mud-palette-text-disabled);opacity:.55\">").Append(RenderInline(WebUtility.HtmlEncode(source[activeEnd..]))).Append("</span>");
        return new MarkupString(output.ToString());
    }

    private static string StripMarkdown(string value)
    {
        var html = Markdown.ToHtml(value, SpeechMarkdownPipeline);
        var text = Regex.Replace(html, @"<!--.*?-->|<[^>]*>", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ", RegexOptions.CultureInvariant).Trim();
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
        if (Session?.PendingNavigatorAction is { } navigatorAction)
        {
            var key = $"{navigatorAction.Action}|{navigatorAction.Route}|{navigatorAction.TargetId}";
            if (!string.Equals(_lastNavigatorAction, key, StringComparison.Ordinal))
            {
                _lastNavigatorAction = key;
                _ = JS.InvokeVoidAsync("ocComposerNavigator.apply", navigatorAction.Action, navigatorAction.Route, navigatorAction.TargetId);
            }
        }
        if (Session is not null)
        {
            var response = Session.Connection == ComposerConnectionState.Streaming
                ? Session.StreamingText
                : Session.Messages.LastOrDefault(message => message.Role == "assistant")?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(response))
                _speechDisplayText = response;
            if (_speechOn)
                QueueSpeechSegments(response, Session.Connection != ComposerConnectionState.Streaming);
            TryNotifySpeechResponseCompleted(response);
        }

        _ = InvokeAsync(StateHasChanged);
    }

    private void QueueSpeechSegments(string response, bool flush)
    {
        if (response.Length < _speechCursor)
            _speechCursor = 0;
        var start = _speechCursor;
        while (start < response.Length)
        {
            var boundary = FindSpeechBoundary(response, start, flush);
            if (boundary <= start)
                break;
            _speechQueue.Enqueue(new SpeechSegment(response[start..boundary].Trim(), start, boundary));
            start = boundary;
        }
        _speechCursor = start;
        if (_speechQueue.Count > 0 && !_speechWorkerRunning)
            _ = DrainSpeechQueueAsync();
    }

    private static int FindSpeechBoundary(string text, int start, bool flush)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' or ';' or ':' or '\n'
                && !IsInsideMarkdownSpan(text, i))
                return i + 1;
            if (i - start >= 240 && !IsInsideMarkdownSpan(text, i))
                return i + 1;
        }
        return flush ? text.Length : start;
    }

    private static bool IsInsideMarkdownSpan(string text, int position)
    {
        var inCode = false;
        var inStrong = false;

        for (var i = 0; i < position; i++)
        {
            if (text[i] == '`')
            {
                inCode = !inCode;
                continue;
            }

            if (inCode || i + 1 >= position)
                continue;
            if ((text[i] == '*' && text[i + 1] == '*')
                || (text[i] == '_' && text[i + 1] == '_'))
            {
                inStrong = !inStrong;
                i++;
            }
        }

        return inCode || inStrong;
    }

    private async Task DrainSpeechQueueAsync()
    {
        _speechWorkerRunning = true;
        try
        {
            while (_speechOn && !_disposed)
            {
                SpeechSegment? segment;
                lock (_speechQueue)
                {
                    if (_speechQueue.Count == 0)
                        break;
                    segment = _speechQueue.Dequeue();
                }
                if (!string.IsNullOrWhiteSpace(segment.Text))
                    await SpeakResponseSegmentAsync(segment).ConfigureAwait(false);
            }
        }
        finally
        {
            _speechWorkerRunning = false;
            if (Session is not null)
            {
                var response = Session.Connection == ComposerConnectionState.Streaming
                    ? Session.StreamingText
                    : Session.Messages.LastOrDefault(message => message.Role == "assistant")?.Text ?? string.Empty;
                TryNotifySpeechResponseCompleted(response);
            }
        }
    }

    private async Task SpeakResponseSegmentAsync(SpeechSegment segment)
    {
        var playbackEpoch = 0L;
        try
        {
            _speechCts?.Dispose();
            _speechCts = new CancellationTokenSource();
            var speechText = StripMarkdown(segment.Text);
            if (string.IsNullOrWhiteSpace(speechText))
                return;
            var result = await Client.SynthesizeSpeechAsync(
                new SpeechSynthesisRequest(speechText, "af_heart", "en-US", "pcm", Session.SpeechEpoch),
                _speechCts.Token).ConfigureAwait(false);
            playbackEpoch = Session.BeginSpeechPlayback();
            _beingSpokenStart = segment.StartCursor;
            _beingSpokenEnd = segment.EndCursor;
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            var dotNetReference = _dotNetRef ??= DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("ocComposerAudio.playPcm", result.AudioBase64, result.SampleRateHz, result.Channels,
                playbackEpoch, dotNetReference, segment.StartCursor, segment.EndCursor).ConfigureAwait(false);
            if (!Session.TryAcceptTtsAudioChunk(playbackEpoch))
                return;
            _spokenResponseCharacters = Math.Max(_spokenResponseCharacters, segment.EndCursor);
            await InvokeAsync(StateHasChanged).ConfigureAwait(false);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or
                JSException or
                JSDisconnectedException or
                HttpRequestException or
                TaskCanceledException or
                NotSupportedException)
        {
            if (!_disposed && _speechOn && ex is not OperationCanceledException)
            {
                await ToggleSpeechAsync().ConfigureAwait(false);
                _speechError = "Voice output is unavailable. The text response is still available.";
                await InvokeAsync(StateHasChanged).ConfigureAwait(false);
            }
        }
        finally
        {
            if (playbackEpoch != 0)
                Session.EndSpeechPlayback(playbackEpoch);
            _beingSpokenStart = -1;
            _beingSpokenEnd = -1;
        }
    }

    [JSInvokable]
    public Task OnSpeechPlaybackStarted(long epoch, int startCharacter, int endCharacter, double durationSeconds) =>
        NotifyClientSpeechEventAsync(epoch, startCharacter, endCharacter, ComposerSpeechPlaybackStage.Started, 0, durationSeconds);

    [JSInvokable]
    public Task OnSpeechPlaybackProgress(long epoch, int startCharacter, int endCharacter, double elapsedSeconds, double durationSeconds) =>
        NotifyClientSpeechEventAsync(epoch, startCharacter, endCharacter, ComposerSpeechPlaybackStage.Progress, elapsedSeconds, durationSeconds);

    [JSInvokable]
    public Task OnSpeechPlaybackEnded(long epoch, int startCharacter, int endCharacter, double durationSeconds) =>
        NotifyClientSpeechEventAsync(epoch, startCharacter, endCharacter, ComposerSpeechPlaybackStage.SegmentCompleted,
            durationSeconds, durationSeconds);

    [JSInvokable]
    public Task OnSpeechPlaybackSynchronizationMeasured(
        long epoch, int startCharacter, int endCharacter, double synchronizationOffsetMilliseconds)
    {
        if (Session is null || !Session.TryAcceptTtsAudioChunk(epoch) ||
            !double.IsFinite(synchronizationOffsetMilliseconds) || synchronizationOffsetMilliseconds < 0 ||
            startCharacter < 0 || endCharacter <= startCharacter)
            return Task.CompletedTask;

        return NotifySpeechPlaybackChangedAsync(new ComposerSpeechPlaybackEvent(
            epoch,
            startCharacter,
            endCharacter,
            ComposerSpeechPlaybackStage.SynchronizationMeasured)
        {
            SynchronizationOffsetMilliseconds = synchronizationOffsetMilliseconds
        });
    }

    private async Task NotifyClientSpeechEventAsync(long epoch, int startCharacter, int endCharacter,
        ComposerSpeechPlaybackStage stage, double elapsedSeconds, double durationSeconds)
    {
        if (Session is null || !Session.TryAcceptTtsAudioChunk(epoch) || !double.IsFinite(elapsedSeconds) ||
            !double.IsFinite(durationSeconds) || elapsedSeconds < 0 || durationSeconds < 0 ||
            startCharacter < 0 || endCharacter < startCharacter)
            return;
        await NotifySpeechPlaybackChangedAsync(new(epoch, startCharacter, endCharacter, stage,
            Math.Min(elapsedSeconds, durationSeconds), durationSeconds)).ConfigureAwait(false);
    }

    private void TryNotifySpeechResponseCompleted(string response)
    {
        if (Session is null || Session.Connection == ComposerConnectionState.Streaming || string.IsNullOrEmpty(response))
            return;
        if (_speechOn && (_speechWorkerRunning || _speechQueue.Count != 0 || _speechCursor < response.Length))
            return;

        var completion = (Session.SessionId, Session.Messages.Count, Session.LastSequence);
        if (_lastSpeechResponseCompleted == completion)
            return;
        _lastSpeechResponseCompleted = completion;
        _ = NotifySpeechPlaybackChangedAsync(new(Session.PlaybackEpoch, 0, response.Length,
            ComposerSpeechPlaybackStage.ResponseCompleted));
    }

    private async Task NotifySpeechPlaybackChangedAsync(ComposerSpeechPlaybackEvent playbackEvent)
    {
        if (_disposed)
            return;
        if (Session is not null)
            await Session.PublishSpeechPlaybackChangedAsync(playbackEvent).ConfigureAwait(false);
        if (!SpeechPlaybackChanged.HasDelegate)
            return;
        try
        { await SpeechPlaybackChanged.InvokeAsync(playbackEvent).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private async Task ToggleSpeechAsync()
    {
        _speechOn = !_speechOn;
        if (!_speechOn)
        {
            _speechCts?.Cancel();
            lock (_speechQueue)
                _speechQueue.Clear();
            Session.TriggerBargeIn();
            try
            { await JS.InvokeVoidAsync("ocComposerAudio.stop").ConfigureAwait(false); }
            catch (Exception) { }
            await NotifySpeechPlaybackChangedAsync(new(Session.PlaybackEpoch, Math.Max(_beingSpokenStart, 0),
                Math.Max(_beingSpokenEnd, 0), ComposerSpeechPlaybackStage.Interrupted)).ConfigureAwait(false);
        }
        else
        {
            _speechError = null;
            _speechCursor = 0;
            _spokenResponseCharacters = 0;
            _beingSpokenStart = -1;
            _beingSpokenEnd = -1;
            OnSessionChanged();
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task OpenSettingsDialogAsync()
    {
        await CloseSettingsMenuAsync();
        var parameters = new DialogParameters<ComposerSettingsDialog>
        {
            { dialog => dialog.SpeechOn, _speechOn },
            { dialog => dialog.MicrophoneOn, _micOn },
            { dialog => dialog.Placement, Placement },
            { dialog => dialog.CloseBehavior, CloseBehavior }
        };
        await DialogService.ShowAsync<ComposerSettingsDialog>("Composer settings", parameters);
    }

    private async Task OnSpeechSwitchChanged(bool value)
    {
        if (_speechOn != value)
            await ToggleSpeechAsync();
        await InvokeAsync(CloseSettingsMenuAsync);
    }

    private async Task OnMicrophoneSwitchChanged(bool value)
    {
        if (_micOn != value)
            await ToggleMicrophoneAsync();
        await InvokeAsync(CloseSettingsMenuAsync);
    }

    private Task CloseSettingsMenuAsync() => _settingsMenu?.CloseMenuAsync() ?? Task.CompletedTask;

    private async Task ToggleMicrophoneAsync()
    {
        _micOn = !_micOn;
        if (!_micOn && _voiceActive)
        {
            _voiceActive = false;
            try
            { await JS.InvokeVoidAsync("ocComposerVoice.stop").ConfigureAwait(false); }
            catch (Exception) { }
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

    private async Task Close()
    {
        if (CloseBehavior is ComposerCloseBehavior.CancelActiveTask or ComposerCloseBehavior.EndSession)
        {
            await StopSending();
        }

        _open = false;
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

        var session = Session ?? throw new InvalidOperationException("MiniComposer requires a composer session.");
        _sending = true;
        _activeTaskId = null;
        _stopRequested = false;
        var sendCts = new CancellationTokenSource();
        _sendCts = sendCts;
        var token = sendCts.Token;
        var draftCleared = false;
        var sendStarted = false;

        try
        {
            await InvokeAsync(StateHasChanged);
            if (string.IsNullOrWhiteSpace(session.SessionId))
            {
                // An explicit send can retry a previously failed session creation.
                if (_sessionCreationFailed)
                {
                    _sessionCreationFailed = false;
                    _sessionCreationTask = null;
                }

                await EnsureSessionAsync().WaitAsync(token);

                if (string.IsNullOrWhiteSpace(session.SessionId))
                    return;
            }

            token.ThrowIfCancellationRequested();
            if (Client is IRealtimeOpenConnectClient)
                await WaitForConnectionAsync(session, session.SessionId!, token);
            _speechCursor = 0;
            _spokenResponseCharacters = 0;
            _speechDisplayText = string.Empty;
            _beingSpokenStart = -1;
            _beingSpokenEnd = -1;
            lock (_speechQueue)
                _speechQueue.Clear();
            _speechCts?.Cancel();
            var sessionId = session.SessionId;
            if (!string.Equals(_pendingSubmissionDraft, text, StringComparison.Ordinal) ||
                !string.Equals(_pendingSubmissionSessionId, sessionId, StringComparison.Ordinal) ||
                _pendingIdempotencyKey is null ||
                _pendingTaskId is null)
            {
                _pendingSubmissionDraft = text;
                _pendingSubmissionSessionId = sessionId;
                _pendingIdempotencyKey = Guid.NewGuid().ToString("N");
                _pendingTaskId = Guid.NewGuid().ToString("N");
                _pendingSubmissionDisplayed = false;
            }

            _draft = string.Empty;
            draftCleared = true;
            _inputRenderKey++;
            await InvokeAsync(StateHasChanged).ConfigureAwait(true);
            await ClearInputAsync().ConfigureAwait(true);
            await AutosizeAsync();
            token.ThrowIfCancellationRequested();
            if (_pendingSubmissionDisplayed)
                session.ResumeSend();
            else
            {
                session.BeginTrackedSend(text);
                _pendingSubmissionDisplayed = true;
            }
            sendStarted = true;
            token.ThrowIfCancellationRequested();
            _admissionInFlight = true;
            TaskRecord admitted;
            try
            {
                admitted = await Client.CreateMiniComposerTaskAsync(
                    new CreateTaskRequest(_pendingTaskId, _pendingIdempotencyKey, text, sessionId)
                    {
                        ClientConnectionId = session.ConnectionId
                    },
                    token);
            }
            finally
            {
                _admissionInFlight = false;
            }
            if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                return;
            _activeTaskId = admitted.Id;
            session.BindTrackedTask(admitted.Id, sessionId, admitted.State);
            ClearPendingSubmissionIdentity();
            if (_stopRequested && admitted.State is not (AgentRunState.Completed or AgentRunState.Cancelled or AgentRunState.Failed))
                await CancelAdmittedTaskAsync(admitted.Id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (draftCleared && string.IsNullOrEmpty(_draft))
            {
                _draft = text;
                _inputRenderKey++;
            }
            if (sendStarted)
                await InvokeAsync(session.CancelStreaming).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // Keep the user's message available for a deliberate retry. In
            // particular, never turn a failed session-bound send into a
            // detached task by clearing or replacing the current session.
            if (draftCleared && string.IsNullOrEmpty(_draft))
            {
                _draft = text;
                _inputRenderKey++;
            }
            await InvokeAsync(() => session.MarkError(FriendlyError(ex))).ConfigureAwait(true);
        }
        finally
        {
            _sending = false;
            if (ReferenceEquals(_sendCts, sendCts))
                _sendCts = null;
            sendCts.Dispose();
            await InvokeAsync(StateHasChanged).ConfigureAwait(true);
        }
    }

    private void ClearPendingSubmissionIdentity()
    {
        _pendingIdempotencyKey = null;
        _pendingTaskId = null;
        _pendingSubmissionDraft = null;
        _pendingSubmissionSessionId = null;
        _pendingSubmissionDisplayed = false;
    }

    private static async Task WaitForConnectionAsync(ComposerSession session, string sessionId, CancellationToken cancellationToken)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged()
        {
            if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal) ||
                !string.IsNullOrWhiteSpace(session.ConnectionId))
                changed.TrySetResult();
        }

        session.Changed += OnChanged;
        try
        {
            OnChanged();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            if (!string.Equals(session.SessionId, sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The active session changed while connecting.");
        }
        finally
        {
            session.Changed -= OnChanged;
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
        if (!Session.CommitLiveTranscript(text, speechEpoch))
            return;
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
            try
            { await JS.InvokeVoidAsync("ocComposerVoice.stop").ConfigureAwait(false); }
            catch (Exception) { }
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
        if (Session.MicrophoneState == ComposerMicrophoneState.Failed)
            return;
        try
        {
            if (Client is IRealtimeOpenConnectClient)
                await WaitForConnectionAsync(Session, Session.SessionId!, CancellationToken.None).ConfigureAwait(false);
            var ticket = VoiceConnectionTicket?.Invoke(Session.SessionId!);
            await JS.InvokeVoidAsync("ocComposerVoice.start", _dotNetRef, Session.SessionId, epoch, VoiceEndpoint, ticket, Session.ConnectionId).ConfigureAwait(false);
            _voiceActive = true;
        }
        catch (Exception ex) when (ex is JSException or InvalidOperationException or JSDisconnectedException or TimeoutException)
        {
            Session.MarkError("Microphone access or voice transcription is unavailable. Text input remains available.");
        }
    }

    private async Task StopSending()
    {
        if (_admissionInFlight)
        {
            // Wait for the admission response so cancellation targets the server-issued task ID.
            _stopRequested = true;
            return;
        }
        if (_activeTaskId is { } taskId)
        {
            await CancelAdmittedTaskAsync(taskId);
            return;
        }
        try
        {
            _sendCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        Session?.CancelStreaming();
    }

    private async Task CancelAdmittedTaskAsync(TaskId taskId)
    {
        if (!_cancellingTaskIds.Add(taskId))
            return;
        try
        {
            var result = await Client.CancelTaskAsync(taskId);
            if (_activeTaskId == taskId && result.State == AgentRunState.Cancelled)
                Session?.CancelStreaming();
        }
        catch (Exception)
        {
            if (_activeTaskId == taskId)
                Session?.ReportError("Could not stop the reply. Please try again.");
        }
        finally
        {
            _cancellingTaskIds.Remove(taskId);
        }
    }

    private void Retry()
    {
        Session?.DismissError();
        Provider?.Reconnect();
    }

    private Task EnsureSessionAsync()
    {
        if (!string.IsNullOrWhiteSpace(Session.SessionId))
            return Task.CompletedTask;
        if (_sessionCreationFailed)
            return Task.CompletedTask;
        if (_sessionCreationTask is { IsCompleted: false } pendingCreation)
            return pendingCreation;
        return _sessionCreationTask ??= CreateSessionAsync();
    }

    private async Task CreateSessionAsync()
    {
        try
        {
            var created = await Client.CreateSessionAsync().ConfigureAwait(false);
            await InvokeAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(Session.SessionId))
                    Session.AttachSession(created.Id.Value.ToString());
            });
        }
        catch
        {
            // A completed failed task remains cached until an explicit action
            // (Send) retries, so each render cannot recreate the same failure.
            _sessionCreationFailed = true;
            throw;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        // Server prerendering has no JS interop: only attach once interactive.
        if ((_open || IsAttached) && !_disposed)
        {
            try
            {
                await EnsureSessionAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await InvokeAsync(() => Session.MarkError(FriendlyError(ex)));
            }
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
        OpenConnectClientException { StatusCodeValue: 400 } => "Your message could not be submitted. Check it and try again.",
        OpenConnectClientException { StatusCodeValue: 403 } => "You don't have access to submit a message in this conversation.",
        OpenConnectClientException { StatusCodeValue: 404 } => "This conversation is no longer available. Reopen it before trying again.",
        OpenConnectClientException { StatusCodeValue: 409 } => "This message conflicts with a request already in progress. Wait a moment, then try again.",
        OpenConnectClientException { StatusCodeValue: 503 } => "The assistant is at capacity. Your draft is saved; try again shortly.",
        OpenConnectClientException => "The assistant could not accept your message. Your draft is saved; try again.",
        TimeoutException => "The assistant is still connecting. Your draft is saved; try again shortly.",
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
            _speechCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        _sendCts?.Dispose();
        _speechCts?.Dispose();
        await DetachJsAsync(dragOnly: false).ConfigureAwait(false);
        _dotNetRef?.Dispose();
        GC.SuppressFinalize(this);
    }
}
