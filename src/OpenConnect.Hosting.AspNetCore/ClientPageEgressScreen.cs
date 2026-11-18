using System.Text;
using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Hosting.AspNetCore;

internal enum ClientPageScreenResult { Allowed, Blocked, Unavailable }

/// <summary>Screens decoded client page content before it crosses a gateway trust boundary.</summary>
internal static class ClientPageEgressScreen
{
    internal static async ValueTask<ClientPageScreenResult> EvaluateAsync(
        string payloadJson, SessionRecord session, IEgressGate? gate, CancellationToken cancellationToken)
    {
        if (gate is null) return ClientPageScreenResult.Unavailable;
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > 65_536) return ClientPageScreenResult.Blocked;
        JsonDocument parsedPage;
        try { parsedPage = JsonDocument.Parse(payloadJson); }
        catch (JsonException) { return ClientPageScreenResult.Blocked; }
        using var page = parsedPage;
        if (page.RootElement.ValueKind != JsonValueKind.Object) return ClientPageScreenResult.Blocked;
        var content = new StringBuilder();
        var pendingValues = new Stack<JsonElement>();
        pendingValues.Push(page.RootElement);
        while (pendingValues.TryPop(out var item))
        {
            if (item.ValueKind == JsonValueKind.String) content.AppendLine(item.GetString());
            else if (item.ValueKind == JsonValueKind.Array)
                foreach (var child in item.EnumerateArray()) pendingValues.Push(child);
            else if (item.ValueKind == JsonValueKind.Object)
                foreach (var property in item.EnumerateObject())
                {
                    content.AppendLine(property.Name);
                    pendingValues.Push(property.Value);
                }
        }

        var decodedContent = content.ToString();
        try
        {
            var decision = await gate.EvaluateAsync(decodedContent,
                new EgressContext(session.TenantId, session.PrincipalId, session.Id, EgressDestination.Tool), cancellationToken)
                .ConfigureAwait(false);
            return decision is not null && decision.Allowed &&
                string.Equals(decision.SanitizedContent, decodedContent, StringComparison.Ordinal)
                ? ClientPageScreenResult.Allowed : ClientPageScreenResult.Blocked;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return ClientPageScreenResult.Unavailable; }
    }
}
