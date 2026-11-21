using OpenConnect.Abstractions;
using OpenConnect.Privacy.Policy;
using OpenConnect.Privacy.Rules;

namespace OpenConnect.Privacy.Gates;

/// <summary>Scans outgoing model responses and tool results for supported sensitive values.</summary>
public sealed class OutgoingContentScanner
{
    private static readonly RulesSecurityDetector Detector = new();
    private readonly PolicyEngine _policy;

    /// <summary>Creates a scanner using the default local rules detector.</summary>
    public OutgoingContentScanner() : this(Detector) { }

    internal OutgoingContentScanner(ISecurityDetector detector) => _policy = new PolicyEngine(detector ?? throw new ArgumentNullException(nameof(detector)));

    /// <summary>Scans outgoing content and returns an egress decision.</summary>
    /// <param name="content">Outgoing content to scan.</param>
    /// <param name="context">Egress context.</param>
    /// <param name="failClosed">Whether to block immediately if any sensitive value is found.</param>
    /// <returns>Decision indicating whether outgoing content passed the configured inspection.</returns>
    public EgressDecision ScanOutgoing(string content, EgressContext context, bool failClosed = true)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);

        EgressDecision result;
        try { result = _policy.EvaluateAsync(content, context, FirewallMode.StrictLocal, true).GetAwaiter().GetResult(); }
        catch { return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure"); }
        if (result is null || !result.Allowed || result.SanitizedContent is null || result.DetectedEntities is null)
            return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure");
        if (result.DetectedEntities.Count == 0 && !string.Equals(result.SanitizedContent, content, StringComparison.Ordinal))
            return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure");
        if (content.Contains('%'))
        {
            if (content.Length > 1_000_000)
                return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure");
            var decoded = content;
            try
            {
                for (var depth = 0; depth < 2 && decoded.Contains('%'); depth++)
                {
                    var next = Uri.UnescapeDataString(decoded);
                    if (next == decoded) break;
                    decoded = next;
                    var inspected = _policy.EvaluateAsync(decoded, context, FirewallMode.StrictLocal, true).GetAwaiter().GetResult();
                    if (inspected is null || !inspected.Allowed || inspected.SanitizedContent is null || inspected.DetectedEntities is null)
                        return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure");
                    if (inspected.DetectedEntities.Count > 0 || inspected.SanitizedContent != decoded)
                        return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "encoded_sensitive_value_detected");
                }
                if (decoded.Contains('%') && Uri.UnescapeDataString(decoded) != decoded)
                    return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure");
            }
            catch { return new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure"); }
        }
        if (result.DetectedEntities.Count == 0)
            return new EgressDecision(true, content, result.DetectedEntities, SecurityPolicyAction.Allow, "clean");

        return failClosed
            ? new EgressDecision(false, null, result.DetectedEntities, SecurityPolicyAction.Block, "literal_sensitive_value_detected")
            : new EgressDecision(true, result.SanitizedContent, result.DetectedEntities, SecurityPolicyAction.Mask, "outgoing_content_sanitized");
    }
}
