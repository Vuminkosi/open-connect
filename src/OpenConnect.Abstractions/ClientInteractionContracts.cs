namespace OpenConnect.Abstractions;

/// <summary>A request for the connected client to perform a host-authorized interaction.</summary>
public sealed record ClientInteractionRequest(
    string CorrelationId,
    string Operation,
    string? TargetId = null,
    string? Action = null,
    string? Value = null,
    string? RouteId = null,
    string? StateName = null,
    string? StateValue = null);

/// <summary>The connected client's serialized result for one interaction request.</summary>
public sealed record ClientInteractionResponse(
    string CorrelationId,
    string ResultKind,
    string PayloadJson);

/// <summary>Event types used to correlate agent requests with connected-client results.</summary>
public static class ClientInteractionEventTypes
{
    public const string Request = "liveui.request";
    public const string Response = "liveui.response";
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
