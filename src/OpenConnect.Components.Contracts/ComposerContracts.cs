namespace OpenConnect.Components.Contracts;

/// <summary>
/// Chat message surfaced by the composer. Plain text only; model output is
/// never interpreted as markup.
/// </summary>
public sealed record ComposerMessage(
    string Role,
    string Text,
    long Sequence = 0,
    ComposerMessageState State = ComposerMessageState.Received);

public enum ComposerMessageState
{
    Sending,
    Streaming,
    Received,
    Cancelled,
    Failed,
}

/// <summary>
/// Connection status for the session event stream.
/// </summary>
public enum ComposerConnectionState
{
    Idle,
    Connecting,
    Streaming,
    Reconnecting,
    Error,
}
