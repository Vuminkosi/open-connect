using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using MudBlazor;
using OpenConnect.Client;
using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Floating assistant composer. Renders a MudBlazor FAB fixed bottom-right and a
/// popover with input, submit, streaming deltas, loading/error/cancel states,
/// suggestion chips, and safe component proposals. Requires the host to provide
/// MudBlazor providers; this component never registers them.
/// </summary>
public partial class MiniComposer : ComponentBase, IDisposable
{
    private bool _open;
    private bool _openedOnce;
    private string _draft = string.Empty;
    private CancellationTokenSource? _sendCts;
    private MudTextField<string>? _inputRef;

    [Inject] private IOpenConnectClient Client { get; set; } = default!;

    [CascadingParameter] private ComposerSession Session { get; set; } = default!;

    [CascadingParameter(Name = "OpenConnectProvider")] private OpenConnectProvider? Provider { get; set; }

    /// <summary>Optional suggestion chips shown above the input.</summary>
    [Parameter] public IReadOnlyList<string>? Suggestions { get; set; }

    [Parameter] public string Placeholder { get; set; } = "Ask the assistant…";

    private bool IsLoading => Session is not null
        && (Session.Connection == ComposerConnectionState.Streaming
            || Session.Connection == ComposerConnectionState.Connecting);

    private bool CanSend => !IsLoading && !string.IsNullOrWhiteSpace(_draft) && Session?.SessionId is not null;

    private IReadOnlyList<ComposerMessage> VisibleMessages => Session?.Messages ?? Array.Empty<ComposerMessage>();

    private string StatusText => Session switch
    {
        null => "Not connected",
        { Connection: ComposerConnectionState.Streaming } => "Replying…",
        { Connection: ComposerConnectionState.Connecting } => "Connecting…",
        { Connection: ComposerConnectionState.Reconnecting } => "Reconnecting…",
        { Connection: ComposerConnectionState.Error } => "Connection issue",
        _ => Session.SessionId is null ? "No session" : "Ready",
    };

    protected override void OnInitialized()
    {
        Session!.Changed += OnSessionChanged;
    }

    private void OnSessionChanged() => _ = InvokeAsync(StateHasChanged);

    private void Toggle()
    {
        _open = !_open;
        _openedOnce = _openedOnce || _open;
    }

    private void Close() => _open = false;

    private Task UseSuggestion(string suggestion)
    {
        _draft = suggestion;
        return SendAsync();
    }

    private async Task HandleKeyDown(KeyboardEventArgs args)
    {
        if (args.Key == "Enter" && (args.CtrlKey || args.MetaKey))
        {
            await SendAsync().ConfigureAwait(false);
        }
    }

    private async Task SendAsync()
    {
        var text = _draft.Trim();
        if (string.IsNullOrWhiteSpace(text) || Session?.SessionId is null || IsLoading)
        {
            return;
        }

        _sendCts?.Cancel();
        _sendCts?.Dispose();
        _sendCts = new CancellationTokenSource();
        var token = _sendCts.Token;

        Session.BeginSend(text);
        _draft = string.Empty;

        try
        {
            await Client.CreateTaskAsync(
                new CreateTaskRequest(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), text, Session.SessionId),
                token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            await InvokeAsync(Session.CancelStreaming).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            await InvokeAsync(() => Session.MarkError(FriendlyError(ex))).ConfigureAwait(true);
        }
    }

    private void CancelSend()
    {
        try
        {
            _sendCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        Session?.CancelStreaming();
    }

    private void Retry() => Provider?.Reconnect();

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_open && _openedOnce && _inputRef is not null)
        {
            try
            {
                await _inputRef.FocusAsync().ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }
        }

        await base.OnAfterRenderAsync(firstRender).ConfigureAwait(false);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        OpenConnectClientException clientEx => string.IsNullOrWhiteSpace(clientEx.Message)
            ? "The assistant could not complete the request. Try again."
            : clientEx.Message,
        HttpRequestException => "Could not reach the assistant service. Check your connection and retry.",
        _ => "Something went wrong sending your message. Try again.",
    };

    public void Dispose()
    {
        Session!.Changed -= OnSessionChanged;
        try
        {
            _sendCts?.Cancel();
        }
        catch (ObjectDisposedException) { }

        _sendCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
