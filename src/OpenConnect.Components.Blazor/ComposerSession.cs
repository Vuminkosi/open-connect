using OpenConnect.Abstractions;
using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// UI-agnostic session state machine. Pure C# with no Blazor dependencies so it
/// is fully unit-testable without bUnit. The provider owns one instance and
/// cascades it; the composer reads and mutates it through these methods.
/// </summary>
public sealed class ComposerSession
{
    private readonly object _gate = new();
    private readonly List<ComposerMessage> _messages = new();
    private readonly List<ComponentProposal> _proposals = new();
    private readonly List<string> _errors = new();
    private string? _streamingRole;
    private string _streamingText = string.Empty;

    public string? SessionId { get; private set; }

    public ComposerConnectionState Connection { get; private set; } = ComposerConnectionState.Idle;

    public IReadOnlyList<ComposerMessage> Messages
    {
        get { lock (_gate) { return _messages.ToList(); } }
    }

    public IReadOnlyList<ComponentProposal> Proposals
    {
        get { lock (_gate) { return _proposals.ToList(); } }
    }

    public string? LastError
    {
        get { lock (_gate) { return _errors.Count == 0 ? null : _errors[^1]; } }
    }

    public long LastSequence { get; private set; }

    public string StreamingText
    {
        get { lock (_gate) { return _streamingText; } }
    }

    public event Action? Changed;

    public void AttachSession(string sessionId)
    {
        lock (_gate)
        {
            SessionId = sessionId;
            Connection = ComposerConnectionState.Idle;
            LastSequence = 0;
            _messages.Clear();
            _proposals.Clear();
            _errors.Clear();
            _streamingText = string.Empty;
            _streamingRole = null;
        }

        Notify();
    }

    public void BeginSend(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        lock (_gate)
        {
            _messages.Add(new ComposerMessage("user", text.Trim(), LastSequence, ComposerMessageState.Received));
            _streamingRole = "assistant";
            _streamingText = string.Empty;
            Connection = ComposerConnectionState.Streaming;
        }

        Notify();
    }

    /// <summary>
    /// Applies one gateway event. Returns true when the UI should re-render.
    /// Unknown event types are ignored (never rendered as markup).
    /// </summary>
    public bool ApplyEvent(EventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var notify = true;
        lock (_gate)
        {
            if (record.Sequence > LastSequence)
            {
                LastSequence = record.Sequence;
            }

            var type = record.Type ?? string.Empty;
            if (type.EndsWith("delta", StringComparison.OrdinalIgnoreCase)
                || type.Equals("message", StringComparison.OrdinalIgnoreCase))
            {
                _streamingText += record.Data ?? string.Empty;
                Connection = ComposerConnectionState.Streaming;
            }
            else if (type.EndsWith("completed", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("done", StringComparison.OrdinalIgnoreCase))
            {
                if (_streamingText.Length > 0 || _streamingRole is not null)
                {
                    _messages.Add(new ComposerMessage(_streamingRole ?? "assistant", _streamingText, record.Sequence));
                    _streamingText = string.Empty;
                    _streamingRole = null;
                }

                if (Connection == ComposerConnectionState.Streaming)
                {
                    Connection = ComposerConnectionState.Idle;
                }
            }
            else if (type.EndsWith("failed", StringComparison.OrdinalIgnoreCase)
                || type.EndsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                _errors.Add(string.IsNullOrWhiteSpace(record.Data) ? "The assistant could not complete the request." : record.Data);
                _messages.Add(new ComposerMessage("assistant", _streamingText, record.Sequence, ComposerMessageState.Failed));
                _streamingText = string.Empty;
                _streamingRole = null;
                Connection = ComposerConnectionState.Error;
            }
            else if (type.EndsWith("proposal", StringComparison.OrdinalIgnoreCase))
            {
                var proposal = TryParseProposal(record.Data);
                if (proposal is not null)
                {
                    _proposals.Add(proposal);
                }
                else
                {
                    notify = false;
                }
            }
            else
            {
                notify = false;
            }
        }

        if (notify)
        {
            Notify();
        }

        return notify;
    }

    public void MarkConnecting()
    {
        lock (_gate)
        {
            Connection = Connection == ComposerConnectionState.Streaming
                ? ComposerConnectionState.Streaming
                : ComposerConnectionState.Connecting;
        }

        Notify();
    }

    public void MarkReconnecting(string? message = null)
    {
        lock (_gate)
        {
            Connection = ComposerConnectionState.Reconnecting;
            if (!string.IsNullOrWhiteSpace(message))
            {
                _errors.Add(message!);
            }
        }

        Notify();
    }

    public void MarkError(string message)
    {
        lock (_gate)
        {
            Connection = ComposerConnectionState.Error;
            _errors.Add(string.IsNullOrWhiteSpace(message) ? "Connection failed." : message);
        }

        Notify();
    }

    public void CancelStreaming()
    {
        lock (_gate)
        {
            if (_streamingText.Length > 0 || _streamingRole is not null)
            {
                _messages.Add(new ComposerMessage(_streamingRole ?? "assistant", _streamingText, LastSequence, ComposerMessageState.Cancelled));
            }

            _streamingText = string.Empty;
            _streamingRole = null;
            if (Connection == ComposerConnectionState.Streaming)
            {
                Connection = ComposerConnectionState.Idle;
            }
        }

        Notify();
    }

    private static ComponentProposal? TryParseProposal(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
        {
            return null;
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            if (!doc.RootElement.TryGetProperty("name", out var nameProp)
                || nameProp.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var name = nameProp.GetString();
            if (string.IsNullOrWhiteSpace(name) || !OpenConnectComponentDescriptor.IsValidName(name))
            {
                return null;
            }

            string? parameters = null;
            if (doc.RootElement.TryGetProperty("parameters", out var parametersProp)
                && parametersProp.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                parameters = parametersProp.GetRawText();
            }

            return new ComponentProposal(name, parameters);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Notify() => Changed?.Invoke();
}
