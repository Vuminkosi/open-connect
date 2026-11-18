using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using OpenConnect.Privacy;

namespace OpenConnect.LiveUI.Inference;

/// <summary>Prepares a LiveUI planning request for an authenticated worker egress boundary.</summary>
public static class LiveUIWorkerPrivacyScreen
{
    /// <summary>Sanitizes intent and rejects worker metadata that privacy policy would alter or block.</summary>
    public static async ValueTask<LiveUIInferenceRequest> PrepareAsync(LiveUIInferenceRequest request,
        IPrivacyFirewall firewall, EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(context);
        if (context.Destination != EgressDestination.Worker)
            throw new ArgumentException("The egress context must target a worker.", nameof(context));
        if (!string.Equals(request.SessionId, context.Session.Value.ToString(), StringComparison.Ordinal))
            throw new ArgumentException("The worker egress context must match the request session.", nameof(context));
        request = request with
        {
            Capabilities = request.Capabilities.Select(capability => capability with { Actions = capability.Actions.ToArray() }).ToArray()
        };

        var intent = await firewall.EvaluateAsync(request.Intent, context, cancellationToken).ConfigureAwait(false);
        if (intent is null || !intent.Allowed || intent.SanitizedContent is null)
            throw new PrivacyBlockedException("LiveUI worker inference was blocked by privacy policy.");

        var metadata = new StringBuilder();
        metadata.AppendLine(request.RouteId);
        foreach (var capability in request.Capabilities)
        {
            metadata.AppendLine(capability.TargetId);
            if (capability.Label is not null) metadata.AppendLine(capability.Label);
            foreach (var action in capability.Actions) metadata.AppendLine(action);
        }
        var content = metadata.ToString();
        var screened = await firewall.EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false);
        if (screened is null || !screened.Allowed || !string.Equals(screened.SanitizedContent, content, StringComparison.Ordinal))
            throw new PrivacyBlockedException("LiveUI worker metadata was blocked by privacy policy.");

        var prepared = request with { Intent = intent.SanitizedContent };
        return prepared with { WorkerScreenedDigest = ComputeDigest(prepared) };
    }

    internal static string ComputeDigest(LiveUIInferenceRequest request) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.Intent, request.SessionId, request.RouteId, request.SchemaVersion, request.ManifestHash,
            request.RequestId, request.ExecutionId,
            Capabilities = request.Capabilities.Select(item => new { item.TargetId, item.Label, Actions = item.Actions.ToArray() }).ToArray()
        })));
}
