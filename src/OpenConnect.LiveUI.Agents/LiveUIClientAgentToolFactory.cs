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
            AIFunctionFactory.Create(tools.NavigateAsync, "liveui_navigate",
                "Navigates to a route from the compiled application manifest and verifies readiness."),
            AIFunctionFactory.Create(tools.InspectAsync, "liveui_inspect",
                "Inspects a compiled target and returns a privacy-safe verified receipt."),
            AIFunctionFactory.Create(tools.ExecuteAsync, "liveui_execute",
                "Executes one authorized action on a compiled UI target and returns its verified receipt."),
            AIFunctionFactory.Create(tools.AwaitStateAsync, "liveui_await_state",
                "Waits for a supported state on a compiled UI target and returns a verified receipt."),
            AIFunctionFactory.Create(tools.HighlightAsync, "liveui_highlight",
                "Highlights a compiled target without opening a Navigator tour popup."),
            AIFunctionFactory.Create(tools.ClearHighlightAsync, "liveui_clear_highlight",
                "Clears LiveUI highlighting from the current page.")
        ];
    }

    private sealed class SessionTools(IClientInteractionBroker interactionBroker, SessionId sessionId, TimeSpan timeout)
    {
        public Task<JsonElement> GetCurrentPageAsync(CancellationToken cancellationToken = default) =>
            RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "get_current_page"), "page", cancellationToken);

        public Task<JsonElement> NavigateAsync(string routeId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(routeId) || routeId.Length > 128)
                return Task.FromResult(JsonSerializer.SerializeToElement(new { error = "invalid_request" }));
            return RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "navigate", RouteId: routeId), "action", cancellationToken);
        }

        public Task<JsonElement> InspectAsync(string targetId, CancellationToken cancellationToken = default) =>
            RequestTargetOperationAsync("inspect", targetId, cancellationToken);

        public Task<JsonElement> ExecuteAsync(string targetId, string action, string? value = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 256 ||
                string.IsNullOrWhiteSpace(action) || action.Length > 32 || value is { Length: > 2_000 })
                return Task.FromResult(JsonSerializer.SerializeToElement(new { error = "invalid_request" }));

            return RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "execute", targetId, action, value),
                "action", cancellationToken);
        }

        public Task<JsonElement> AwaitStateAsync(string targetId, string stateName, string? stateValue = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 256 ||
                string.IsNullOrWhiteSpace(stateName) || stateName.Length > 64 || stateValue is { Length: > 256 })
                return Task.FromResult(JsonSerializer.SerializeToElement(new { error = "invalid_request" }));
            return RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "await_state",
                targetId, StateName: stateName, StateValue: stateValue), "action", cancellationToken);
        }

        public Task<JsonElement> HighlightAsync(string targetId, CancellationToken cancellationToken = default) =>
            RequestTargetOperationAsync("highlight", targetId, cancellationToken);

        public Task<JsonElement> ClearHighlightAsync(CancellationToken cancellationToken = default) =>
            RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), "clear_highlight"), "action", cancellationToken);

        private Task<JsonElement> RequestTargetOperationAsync(string operation, string targetId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(targetId) || targetId.Length > 256)
                return Task.FromResult(JsonSerializer.SerializeToElement(new { error = "invalid_request" }));
            return RequestAsync(new ClientInteractionRequest(Guid.NewGuid().ToString("D"), operation, targetId),
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
