using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Policy;

/// <summary>Evaluates egress requests using the configured firewall mode and detector.</summary>
public sealed class PolicyEngine(ISecurityDetector? detector = null)
{
    /// <summary>Evaluates whether content can proceed and applies detector sanitization.</summary>
    /// <param name="content">Content to evaluate.</param>
    /// <param name="context">Tenant, principal, session, and destination context.</param>
    /// <param name="mode">Firewall mode to apply.</param>
    /// <param name="enabled">Whether enforcement is enabled.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The resulting egress decision.</returns>
    public async ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, FirewallMode mode, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        if (!enabled || mode == FirewallMode.Disabled)
            return new(true, content, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "privacy_enforcement_disabled");
        if (mode == FirewallMode.LocalOnly && context.Destination != EgressDestination.Worker)
            return Block("remote_egress_forbidden");
        if (detector is null)
            return Block("required_detector_unavailable");

        DetectorResult result;
        try { result = await detector.DetectAndSanitizeAsync(content, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Block("detector_failure"); }
        if (!result.Available || result.SanitizedContent is null || result.DetectedEntities is null)
            return Block("required_detector_unavailable");
        return new(true, result.SanitizedContent, result.DetectedEntities, SecurityPolicyAction.Mask, "sanitized");
    }

    private static EgressDecision Block(string reason) => new(false, null, Array.Empty<EntitySpan>(), SecurityPolicyAction.Block, reason);
}
