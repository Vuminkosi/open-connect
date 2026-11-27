using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Requests an untrusted Cloud LiveUI proposal from an authorized application host.</summary>
public sealed class CloudLiveUIInferenceProvider(
    ICloudLiveUIHostAuthorizer authorizer,
    IEgressGate gate,
    ICloudLiveUITransport transport,
    IOptions<LiveUIInferenceOptions> options) : ILiveUIGatewayInferenceProvider
{
    public LiveUIInferenceProviderKind Kind => LiveUIInferenceProviderKind.Gateway;

    public async Task<LiveUIInferenceAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var ready = await transport.IsReadyAsync(cancellationToken).ConfigureAwait(false);
        return new(Kind, ready ? LiveUIInferenceAvailabilityStatus.Available : LiveUIInferenceAvailabilityStatus.Unavailable,
            SupportsStructuredOutput: true, SupportsCancellation: true, Reason: ready ? null : "gateway_unavailable");
    }

    public async Task<LiveUIInferenceResult> GeneratePlanAsync(LiveUIInferenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Guid.TryParse(request.ExecutionId, out var executionId) || executionId == Guid.Empty ||
            request.Capabilities is null || request.Capabilities.Any(item => item is null || item.Actions is null))
            return new(Kind, null, "invalid_request");
        var snapshot = request with { Capabilities = request.Capabilities.Select(item => item with { Actions = item.Actions.ToArray() }).ToArray() };
        EgressContext? context;
        try
        { context = await authorizer.AuthorizeAsync(snapshot, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new UnauthorizedAccessException("The LiveUI session is unavailable."); }
        if (context is null)
            throw new UnauthorizedAccessException("The LiveUI session is unavailable.");
        var prepared = await CloudLiveUIPrivacyScreen.PrepareAsync(snapshot, gate, context, cancellationToken).ConfigureAwait(false);
        var maximumActions = options.Value.Planning.MaxActions;
        if (maximumActions is < 1 or > 32)
            return new(Kind, null, "invalid_request");
        var input = new
        {
            schemaVersion = "openconnect.cloud.liveui/v1",
            requestId = executionId.ToString("D"),
            capability = "inference.liveui",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            input = new
            {
                schemaVersion = prepared.SchemaVersion,
                intent = prepared.Intent,
                context = new
                {
                    route = prepared.RouteId,
                    availableActions = prepared.Capabilities.SelectMany(item => item.Actions.Select(action =>
                    new { target = item.TargetId, action, label = item.Label })).ToArray()
                },
                generation = new { maxActions = maximumActions, maxOutputTokens = 256 }
            }
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(input);
        byte[]? output = null;
        try
        {
            if (body.Length > 131072)
                return new(Kind, null, "invalid_request");
            var response = await transport.SendAsync(body, cancellationToken).ConfigureAwait(false);
            output = response.Body;
            if (response.StatusCode is 401 or 403)
                throw new UnauthorizedAccessException("Cloud LiveUI access was denied.");
            if (response.StatusCode == 429)
                return new(Kind, null, "rate_limited");
            if (response.StatusCode == 409)
                return new(Kind, null, "conflict");
            if (response.StatusCode != 200)
                return new(Kind, null, "gateway_unavailable");
            var plan = ParsePlan(output, executionId, prepared, maximumActions);
            if (plan is null)
                return new(Kind, null, "invalid_plan");
            var outputFields = plan.Actions.SelectMany(action => action.Value is null
                ? new[] { action.Type, action.TargetId } : new[] { action.Type, action.TargetId, action.Value }).ToArray();
            foreach (var field in outputFields.Append(string.Concat(outputFields)))
            {
                EgressDecision decision;
                try
                { decision = await gate.EvaluateAsync(field, context with { Destination = EgressDestination.Output }, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch { throw new PrivacyBlockedException("LiveUI Cloud output was blocked by privacy policy."); }
                if (decision is null || !decision.Allowed || !string.Equals(decision.SanitizedContent, field, StringComparison.Ordinal))
                    throw new PrivacyBlockedException("LiveUI Cloud output was blocked by privacy policy.");
            }
            return new(Kind, plan, "completed");
        }
        finally { Array.Clear(body); if (output is not null) Array.Clear(output); }
    }

    private static LiveUIActionPlan? ParsePlan(byte[]? bytes, Guid executionId, LiveUIInferenceRequest request, int maximumActions)
    {
        if (bytes is null || bytes.Length is < 1 or > 262144)
            return null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!ExactProperties(root, "schemaVersion", "requestId", "traceId", "provider", "plan") ||
                root.GetProperty("schemaVersion").GetString() != "openconnect.cloud.liveui.result/v1" ||
                !Guid.TryParseExact(root.GetProperty("requestId").GetString(), "D", out var returnedId) || returnedId != executionId ||
                root.GetProperty("traceId").GetString() is not { Length: 32 } trace ||
                trace.All(character => character == '0') || trace.Any(character => !char.IsAsciiHexDigitLower(character)) ||
                root.GetProperty("provider").GetString() is not ("deterministic" or "qwen3-0.6b"))
                return null;
            var value = root.GetProperty("plan");
            if (!ExactProperties(value, "schemaVersion", "actions") || value.GetProperty("schemaVersion").GetString() != request.SchemaVersion ||
                value.GetProperty("actions").ValueKind != JsonValueKind.Array)
                return null;
            var actions = new List<LiveUIPlannedAction>();
            foreach (var action in value.GetProperty("actions").EnumerateArray())
            {
                if (actions.Count >= maximumActions || action.ValueKind != JsonValueKind.Object)
                    return null;
                var hasValue = action.TryGetProperty("value", out var actionValue);
                if (!ExactProperties(action, hasValue ? ["type", "target", "value"] : ["type", "target"]))
                    return null;
                var type = action.GetProperty("type").GetString();
                var target = action.GetProperty("target").GetString();
                if (type is null || target is null || (hasValue && actionValue.ValueKind != JsonValueKind.String))
                    return null;
                actions.Add(new(type, target, hasValue ? actionValue.GetString() : null));
            }
            var plan = new LiveUIActionPlan(request.SchemaVersion, actions);
            return new LiveUIActionPlanValidator().Validate(request, plan, maximumActions, out _) ? plan : null;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static bool ExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return false;
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !found.Add(property.Name))
                return false;
        return found.Count == names.Length;
    }
}
