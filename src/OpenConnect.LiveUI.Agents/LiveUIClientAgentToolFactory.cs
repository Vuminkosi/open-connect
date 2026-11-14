using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenConnect.Abstractions;

namespace OpenConnect.LiveUI.Agents;

/// <summary>Creates MAF tools that ask the authenticated connected client to run LiveUI actions.</summary>
public static class LiveUIClientAgentToolFactory
{
    /// <summary>
    /// Creates tools for a trusted session. The session identifier is supplied by the host and is never a model argument.
    /// Each tool waits for the matching client response before returning to the model.
    /// </summary>
    public static IReadOnlyList<AITool> CreateTools(
        IClientInteractionBroker interactionBroker,
        SessionId sessionId,
        TimeSpan? responseTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(interactionBroker);
        if (sessionId.Value == default) throw new ArgumentException("A trusted session ID is required.", nameof(sessionId));
        var timeout = responseTimeout ?? TimeSpan.FromSeconds(45);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(responseTimeout));

        var tools = new SessionTools(interactionBroker, sessionId, timeout);
        return
        [
            AIFunctionFactory.Create(tools.GetCurrentPageAsync, "liveui_get_current_page",
                "Returns authorized compiled targets and supported actions on the user's current page."),
            AIFunctionFactory.Create(tools.ExecuteAsync, "liveui_execute",
                "Executes one authorized action on a compiled UI target and returns its verified receipt.")
        ];
    }

    private sealed class SessionTools(IClientInteractionBroker interactionBroker, SessionId sessionId, TimeSpan timeout)
    {
        public Task<JsonElement> GetCurrentPageAsync(CancellationToken cancellationToken = default) =>
            RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_current_page"), "page", cancellationToken);

        public Task<JsonElement> ExecuteAsync(string targetId, string action, string? value = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 256 ||
                string.IsNullOrWhiteSpace(action) || action.Length > 32 || value is { Length: > 2_000 })
                return Task.FromResult(JsonSerializer.SerializeToElement(new { error = "invalid_request" }));

            return RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "execute", targetId, action, value),
                "action", cancellationToken);
        }

        private async Task<JsonElement> RequestAsync(ClientInteractionRequest request, string expectedResultKind,
            CancellationToken cancellationToken)
        {
            var response = await interactionBroker.RequestAsync(sessionId, request, expectedResultKind, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (response is null) return JsonSerializer.SerializeToElement(new { error = "client_response_timeout" });
            if (response.CorrelationId != request.CorrelationId || response.ResultKind != expectedResultKind)
                return JsonSerializer.SerializeToElement(new { error = "client_response_mismatch" });

            try
            {
                using var payload = JsonDocument.Parse(response.PayloadJson);
                if (payload.RootElement.ValueKind == JsonValueKind.Object) return payload.RootElement.Clone();
            }
            catch (JsonException) { }
            return JsonSerializer.SerializeToElement(new { error = "client_response_invalid" });
        }
    }
}
