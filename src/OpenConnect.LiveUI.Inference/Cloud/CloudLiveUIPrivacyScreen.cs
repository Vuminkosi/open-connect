using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Prepares model-visible LiveUI content for an external Cloud boundary.</summary>
public static class CloudLiveUIPrivacyScreen
{
    /// <summary>Checks the host session and privacy policy before a Cloud request is created.</summary>
    public static async ValueTask<LiveUIInferenceRequest> PrepareAsync(
        LiveUIInferenceRequest request, IEgressGate gate, EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Destination != EgressDestination.ExternalModel ||
            !string.Equals(request.SessionId, context.Session.Value.ToString(), StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The LiveUI session is unavailable.");
        if (request.Capabilities is null || request.Capabilities.Any(item => item is null || item.Actions is null))
            throw new ArgumentException("LiveUI capabilities are invalid.", nameof(request));

        // Snapshot mutable application collections before the first asynchronous policy check.
        var capabilities = request.Capabilities.Select(item => item with { Actions = item.Actions.ToArray() }).ToArray();
        var snapshot = request with { Capabilities = capabilities };
        if (snapshot.Intent is null || snapshot.Intent.Length is < 1 or > 16000 ||
            snapshot.SchemaVersion is null || snapshot.SchemaVersion.Length is < 1 or > 64 ||
            snapshot.RouteId is null || snapshot.RouteId.Length is < 1 or > 512 ||
            capabilities.Length > 256 || capabilities.Sum(item => (long)item.Actions.Count) > 256 ||
            capabilities.Any(item => item.TargetId is null || item.TargetId.Length is < 1 or > 512 ||
                item.Label is { Length: > 512 } || item.Actions.Any(action => action is null || action.Length is < 1 or > 64)))
            throw new ArgumentException("LiveUI fields are invalid.", nameof(request));
        var intent = await EvaluateAsync(snapshot.Intent).ConfigureAwait(false);
        if (intent is null || !intent.Allowed || intent.SanitizedContent is null)
            throw new PrivacyBlockedException("LiveUI Cloud input was blocked by privacy policy.");
        if (intent.SanitizedContent.Length is < 1 or > 16000)
            throw new PrivacyBlockedException("LiveUI Cloud input was blocked by privacy policy.");

        var fields = new List<string> { snapshot.SchemaVersion, intent.SanitizedContent, snapshot.RouteId };
        foreach (var capability in capabilities)
        {
            foreach (var action in capability.Actions)
            {
                fields.Add(capability.TargetId);
                fields.Add(action);
                if (capability.Label is not null) fields.Add(capability.Label);
            }
        }
        if (fields.Any(field => field is null))
            throw new ArgumentException("LiveUI fields are invalid.", nameof(request));

        // Individual checks and the concatenated sequence catch values split across fields.
        foreach (var field in fields.Append(string.Concat(fields)))
        {
            var decision = await EvaluateAsync(field).ConfigureAwait(false);
            if (decision is null || !decision.Allowed || !string.Equals(decision.SanitizedContent, field, StringComparison.Ordinal))
                throw new PrivacyBlockedException("LiveUI Cloud input was blocked by privacy policy.");
        }
        return snapshot with { Intent = intent.SanitizedContent };

        async ValueTask<EgressDecision> EvaluateAsync(string content)
        {
            try { return await gate.EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { throw new PrivacyBlockedException("LiveUI Cloud input was blocked by privacy policy."); }
        }
    }
}
