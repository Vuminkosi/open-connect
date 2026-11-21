using OpenConnect.Abstractions;
using System.Text;

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
        try
        { result = await detector.DetectAndSanitizeAsync(content, context, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return Block("detector_failure"); }
        if (result is null || !result.Available || result.SanitizedContent is null || result.DetectedEntities is null)
            return Block("required_detector_unavailable");
        if (result.DetectedEntities.Count > 0)
        {
            if (result.DetectedEntities.Count > 20000 || (long)result.DetectedEntities.Count * result.SanitizedContent.Length > 100_000_000)
                return Block("detector_failure");
            var offsets = new List<int>();
            var utf16Offset = 0;
            foreach (var rune in content.EnumerateRunes()) { offsets.Add(utf16Offset); utf16Offset += rune.Utf16SequenceLength; }
            offsets.Add(utf16Offset);
            var length = offsets.Count - 1;
            if (string.Equals(content, result.SanitizedContent, StringComparison.Ordinal) || result.DetectedEntities.Any(entity =>
                entity is null || entity.Start < 0 || entity.Length <= 0 || (long)entity.Start + entity.Length > length ||
                result.SanitizedContent.AsSpan().Contains(content.AsSpan(offsets[entity.Start],
                    offsets[entity.Start + entity.Length] - offsets[entity.Start]), StringComparison.Ordinal)))
                return Block("detector_failure");
        }
        var action = string.Equals(content, result.SanitizedContent, StringComparison.Ordinal)
            ? SecurityPolicyAction.Allow : SecurityPolicyAction.Mask;
        return new(true, result.SanitizedContent, result.DetectedEntities, action, "sanitized");
    }

    private static EgressDecision Block(string reason) => new(false, null, Array.Empty<EntitySpan>(), SecurityPolicyAction.Block, reason);
}
