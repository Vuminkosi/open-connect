namespace OpenConnect.Abstractions;

/// <summary>A request for the connected client to perform a host-authorized interaction.</summary>
/// <param name="CorrelationId">Unique correlation identifier for the request.</param>
/// <param name="Operation">Allowlisted client operation name.</param>
/// <param name="TargetId">Optional compiled target identifier.</param>
/// <param name="Action">Optional typed action name.</param>
/// <param name="Value">Optional bounded action value.</param>
/// <param name="RouteId">Optional manifest route identifier.</param>
/// <param name="StateName">Optional state condition name.</param>
/// <param name="StateValue">Optional state condition value.</param>
/// <param name="HighlightDurationMilliseconds">Optional temporary highlight duration.</param>
public sealed record ClientInteractionRequest(
    string CorrelationId,
    string Operation,
    string? TargetId = null,
    string? Action = null,
    string? Value = null,
    string? RouteId = null,
    string? StateName = null,
    string? StateValue = null,
    int HighlightDurationMilliseconds = 0)
{
    /// <summary>Optional zero-based UTF-16 response range start for a speech highlight cue.</summary>
    public int? SpeechStartCharacter { get; init; }

    /// <summary>Optional exclusive range end; both range values must be supplied and no greater than 32,000.</summary>
    public int? SpeechEndCharacter { get; init; }

    /// <summary>Optional opaque identifier for the authenticated client connection bound to this interaction.</summary>
    public string? ConnectionId { get; init; }
}

/// <summary>The connected client's serialized result for one interaction request.</summary>
public sealed record ClientInteractionResponse(
    string CorrelationId,
    string ResultKind,
    string PayloadJson)
{
    /// <summary>Optional opaque identifier for the authenticated client connection bound to this response.</summary>
    public string? ConnectionId { get; init; }
}

/// <summary>Cancels one pending connected-client interaction by its request correlation.</summary>
public sealed record ClientInteractionCancellation(string CorrelationId);

/// <summary>Event types used to correlate agent requests with connected-client results.</summary>
public static class ClientInteractionEventTypes
{
    public const string Request = "liveui.request";
    public const string Response = "liveui.response";
    public const string Cancellation = "liveui.cancel";
}

/// <summary>Correlates trusted agent requests with responses from the authenticated client session.</summary>
public interface IClientInteractionBroker
{
    Task<ClientInteractionResponse?> RequestAsync(
        SessionId sessionId,
        ClientInteractionRequest request,
        string expectedResultKind,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
