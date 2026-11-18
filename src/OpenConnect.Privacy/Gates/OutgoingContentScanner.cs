using System.Text.RegularExpressions;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Rules;

namespace OpenConnect.Privacy.Gates;

/// <summary>
/// Scans outgoing model responses and tool results independently for literal sensitive values,
/// preventing accidental leakage of secrets, credit cards, or PII generated or echoed by models.
/// </summary>
public sealed class OutgoingContentScanner
{
    private static readonly Regex SecretRegex = new(@"(?i)\b(?:bearer\s+[a-z0-9_\-\.]{20,}|ghp_[a-z0-9]{36}|sk-[a-z0-9]{20,}|xox[baprs]-[0-9a-z]{10,}|-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----)", RegexOptions.Compiled);
    private static readonly Regex PotentialCardRegex = new(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.Compiled);

    /// <summary>Scans outgoing content and returns an egress decision.</summary>
    /// <param name="content">Outgoing content to scan.</param>
    /// <param name="context">Egress context.</param>
    /// <param name="failClosed">Whether to block immediately if any sensitive value is found.</param>
    /// <returns>Decision indicating whether outgoing content is safe to emit.</returns>
    public EgressDecision ScanOutgoing(string content, EgressContext context, bool failClosed = true)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);

        if (content.Length == 0)
        {
            return new EgressDecision(true, string.Empty, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "clean");
        }

        var detectedEntities = new List<EntitySpan>();

        // 1. Scan for literal secrets
        foreach (Match match in SecretRegex.Matches(content))
        {
            detectedEntities.Add(new EntitySpan(match.Index, match.Length, "SECRET"));
        }

        // 2. Scan for literal payment cards with Luhn check
        foreach (Match match in PotentialCardRegex.Matches(content))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length is >= 13 and <= 19 && RulesSecurityDetector.PassesLuhnCheck(digitsOnly))
            {
                detectedEntities.Add(new EntitySpan(match.Index, match.Length, "PAYMENT_CARD"));
            }
        }

        if (detectedEntities.Count == 0)
        {
            return new EgressDecision(true, content, Array.Empty<EntitySpan>(), SecurityPolicyAction.Allow, "clean");
        }

        if (failClosed)
        {
            return new EgressDecision(false, null, detectedEntities, SecurityPolicyAction.Block, "literal_sensitive_value_detected");
        }

        // Mask detected sensitive spans
        var sanitized = content;
        foreach (var entity in detectedEntities.OrderByDescending(e => e.Start))
        {
            sanitized = sanitized.Remove(entity.Start, entity.Length).Insert(entity.Start, $"[OC_{entity.EntityType}]");
        }

        return new EgressDecision(true, sanitized, detectedEntities, SecurityPolicyAction.Mask, "outgoing_content_sanitized");
    }
}
