using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Tokenization;

namespace OpenConnect.Privacy.Rules;

/// <summary>Represents a custom pattern rule for detecting sensitive content.</summary>
/// <param name="EntityType">The entity type label.</param>
/// <param name="Pattern">The regex pattern to match.</param>
public sealed record CustomDetectorRule(string EntityType, Regex Pattern);

/// <summary>Deterministic local rules detector for PII, secrets, and checksum-validated entities.</summary>
public sealed class RulesSecurityDetector : ISecurityDetector
{
    private static readonly Regex EmailRegex = new(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled);
    private static readonly Regex PhoneRegex = new(@"(?:\+?\d{1,3}[-.\s])?\(?\d{3}\)?[-.\s]\d{3}[-.\s]\d{4}\b", RegexOptions.Compiled);
    private static readonly Regex PotentialCardRegex = new(@"\b(?:\d[ -]*?){13,19}\b", RegexOptions.Compiled);
    private static readonly Regex SecretRegex = new(@"(?i)\b(?:bearer\s+[a-z0-9_\-\.]{20,}|ghp_[a-z0-9]{36}|sk-[a-z0-9]{20,}|xox[baprs]-[0-9a-z]{10,}|-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----)", RegexOptions.Compiled);

    private readonly ITokenVault? _vault;
    private readonly IReadOnlyList<CustomDetectorRule> _customRules;

    /// <summary>Initializes a new instance of <see cref="RulesSecurityDetector"/>.</summary>
    /// <param name="vault">Optional token vault to tokenize detected entities.</param>
    /// <param name="customRules">Optional custom regex rules.</param>
    public RulesSecurityDetector(ITokenVault? vault = null, IReadOnlyList<CustomDetectorRule>? customRules = null)
    {
        _vault = vault;
        _customRules = customRules ?? Array.Empty<CustomDetectorRule>();
    }

    /// <inheritdoc />
    public async ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (content.Length == 0)
        {
            return new DetectorResult(true, string.Empty, Array.Empty<EntitySpan>());
        }

        var matches = new List<RawEntityMatch>();

        // 1. Secrets
        foreach (Match match in SecretRegex.Matches(content))
        {
            matches.Add(new RawEntityMatch(match.Index, match.Length, "SECRET", match.Value));
        }

        // 2. Email
        foreach (Match match in EmailRegex.Matches(content))
        {
            matches.Add(new RawEntityMatch(match.Index, match.Length, "EMAIL", match.Value));
        }

        // 3. Payment cards (with Luhn checksum validation)
        foreach (Match match in PotentialCardRegex.Matches(content))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length is >= 13 and <= 19 && PassesLuhnCheck(digitsOnly))
            {
                matches.Add(new RawEntityMatch(match.Index, match.Length, "PAYMENT_CARD", match.Value));
            }
        }

        // 4. Phone numbers
        foreach (Match match in PhoneRegex.Matches(content))
        {
            var digitsOnly = new string(match.Value.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length is >= 7 and <= 15)
            {
                matches.Add(new RawEntityMatch(match.Index, match.Length, "PHONE", match.Value));
            }
        }

        // 5. Custom rules
        foreach (var rule in _customRules)
        {
            foreach (Match match in rule.Pattern.Matches(content))
            {
                matches.Add(new RawEntityMatch(match.Index, match.Length, rule.EntityType, match.Value));
            }
        }

        if (matches.Count == 0)
        {
            return new DetectorResult(true, content, Array.Empty<EntitySpan>());
        }

        // Deterministically sort and deduplicate overlapping spans (earliest start, then longest length)
        matches.Sort((a, b) =>
        {
            var cmp = a.Start.CompareTo(b.Start);
            return cmp != 0 ? cmp : b.Length.CompareTo(a.Length);
        });

        var nonOverlapping = new List<RawEntityMatch>();
        var lastEnd = -1;
        foreach (var m in matches)
        {
            if (m.Start >= lastEnd)
            {
                nonOverlapping.Add(m);
                lastEnd = m.Start + m.Length;
            }
        }

        // Build sanitized content and calculate Unicode code-point offsets
        var sb = new StringBuilder();
        var detectedSpans = new List<EntitySpan>();
        var currentIndex = 0;
        var scope = new PrivacyScope(context.Tenant, context.Principal, context.Session, "redaction");

        foreach (var entity in nonOverlapping)
        {
            if (entity.Start > currentIndex)
            {
                sb.Append(content, currentIndex, entity.Start - currentIndex);
            }

            string replacement;
            if (_vault is not null)
            {
                replacement = await _vault.StoreAsync(entity.RawValue, entity.EntityType, scope, TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                replacement = $"[OC_{entity.EntityType}]";
            }

            var startOffset = CountUnicodeCodePoints(content, 0, entity.Start);
            var length = CountUnicodeCodePoints(content, entity.Start, entity.Length);
            detectedSpans.Add(new EntitySpan(startOffset, length, entity.EntityType));

            sb.Append(replacement);
            currentIndex = entity.Start + entity.Length;
        }

        if (currentIndex < content.Length)
        {
            sb.Append(content, currentIndex, content.Length - currentIndex);
        }

        return new DetectorResult(true, sb.ToString(), detectedSpans);
    }

    /// <summary>Calculates the number of Unicode code points (scalars) in the specified string segment.</summary>
    public static int CountUnicodeCodePoints(string str, int startIndex, int length)
    {
        if (string.IsNullOrEmpty(str) || length <= 0) return 0;
        var count = 0;
        var end = startIndex + length;
        for (var i = startIndex; i < end; i++)
        {
            if (char.IsHighSurrogate(str[i]) && i + 1 < end && char.IsLowSurrogate(str[i + 1]))
            {
                i++;
            }
            count++;
        }
        return count;
    }

    /// <summary>Validates a number string using the standard Luhn checksum algorithm.</summary>
    public static bool PassesLuhnCheck(string digits)
    {
        if (string.IsNullOrWhiteSpace(digits)) return false;
        var sum = 0;
        var alternate = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var c = digits[i];
            if (c is < '0' or > '9') return false;
            var n = c - '0';
            if (alternate)
            {
                n *= 2;
                if (n > 9) n -= 9;
            }
            sum += n;
            alternate = !alternate;
        }
        return sum % 10 == 0;
    }

    private sealed record RawEntityMatch(int Start, int Length, string EntityType, string RawValue);
}
