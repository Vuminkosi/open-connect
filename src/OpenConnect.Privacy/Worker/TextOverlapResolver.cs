using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Worker;

/// <summary>A deterministically resolved, non-overlapping text span with winning category and strictest policy action.</summary>
public sealed record ResolvedTextSpan(
    int Utf16Start,
    int Utf16Length,
    int ScalarStart,
    int ScalarLength,
    string WinningCategory,
    IReadOnlyList<string> MergedCategories,
    SecurityPolicyAction Action,
    double MaxConfidence);

/// <summary>Result of resolving overlaps and sanitizing text with host tokenization.</summary>
public sealed record TextSanitizationResult(
    bool Allowed,
    string? SanitizedContent,
    SecurityPolicyAction EffectiveAction,
    IReadOnlyList<ResolvedTextSpan> ResolvedSpans,
    IReadOnlyList<EntitySpan> EntitySpans,
    string ReasonCode);

/// <summary>
/// Deterministic host-owned overlap resolver and sanitizer for text findings (FW-030).
/// Prioritizes credentials/secrets and stricter actions, preserves JSON syntax, and prevents
/// secret restoration through generic policies.
/// </summary>
public static class TextOverlapResolver
{
    private static readonly IReadOnlySet<string> SecretCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "SECRET", "CREDENTIAL", "API_KEY", "PRIVATE_KEY", "ACCESS_TOKEN", "PASSWORD"
    };

    /// <summary>Returns whether the given entity category represents a secret/credential.</summary>
    public static bool IsSecretCategory(string? category) =>
        !string.IsNullOrWhiteSpace(category) && SecretCategories.Contains(category);

    /// <summary>Resolves overlapping text findings deterministically, prioritizing secrets/credentials and stricter actions.</summary>
    public static IReadOnlyList<ResolvedTextSpan> ResolveOverlaps(
        IReadOnlyList<(SecurityFinding Finding, Utf16TextSpan Utf16Span)> validatedFindings,
        IReadOnlyDictionary<string, SecurityPolicyAction>? categoryPolicies = null)
    {
        ArgumentNullException.ThrowIfNull(validatedFindings);
        if (validatedFindings.Count == 0)
            return Array.Empty<ResolvedTextSpan>();

        var ordered = validatedFindings
            .OrderBy(item => item.Utf16Span.Start)
            .ThenByDescending(item => item.Utf16Span.Length)
            .ThenByDescending(item => GetCategoryPriority(item.Finding.Category))
            .ThenByDescending(item => GetActionStrictness(ResolveAction(item.Finding, categoryPolicies)))
            .ThenBy(item => item.Finding.Category, StringComparer.Ordinal)
            .ToList();

        var resolved = new List<ResolvedTextSpan>();
        var currentUtf16Start = ordered[0].Utf16Span.Start;
        var currentUtf16End = ordered[0].Utf16Span.Start + ordered[0].Utf16Span.Length;
        var currentScalarStart = ordered[0].Utf16Span.ScalarStart;
        var currentScalarEnd = ordered[0].Utf16Span.ScalarStart + ordered[0].Utf16Span.ScalarLength;
        var currentWinner = ordered[0].Finding.Category;
        var currentWinnerPriority = GetCategoryPriority(currentWinner);
        var currentAction = ResolveAction(ordered[0].Finding, categoryPolicies);
        var currentMaxConf = ordered[0].Finding.Confidence;
        var mergedCategories = new SortedSet<string>(StringComparer.Ordinal) { currentWinner };

        for (var i = 1; i < ordered.Count; i++)
        {
            var (finding, span) = ordered[i];
            var findingAction = ResolveAction(finding, categoryPolicies);
            var findingPriority = GetCategoryPriority(finding.Category);

            if (span.Start < currentUtf16End)
            {
                // Overlapping span: merge range, keep highest-priority category and strictest action.
                var candidateUtf16End = span.Start + span.Length;
                if (candidateUtf16End > currentUtf16End)
                    currentUtf16End = candidateUtf16End;

                var candidateScalarEnd = span.ScalarStart + span.ScalarLength;
                if (candidateScalarEnd > currentScalarEnd)
                    currentScalarEnd = candidateScalarEnd;

                mergedCategories.Add(finding.Category);

                if (findingPriority > currentWinnerPriority ||
                    (findingPriority == currentWinnerPriority && GetActionStrictness(findingAction) > GetActionStrictness(currentAction)) ||
                    (findingPriority == currentWinnerPriority && GetActionStrictness(findingAction) == GetActionStrictness(currentAction) &&
                     string.CompareOrdinal(finding.Category, currentWinner) < 0))
                {
                    currentWinner = finding.Category;
                    currentWinnerPriority = findingPriority;
                }

                if (GetActionStrictness(findingAction) > GetActionStrictness(currentAction))
                    currentAction = findingAction;

                if (finding.Confidence > currentMaxConf)
                    currentMaxConf = finding.Confidence;
            }
            else
            {
                resolved.Add(new ResolvedTextSpan(
                    currentUtf16Start,
                    currentUtf16End - currentUtf16Start,
                    currentScalarStart,
                    currentScalarEnd - currentScalarStart,
                    currentWinner,
                    mergedCategories.ToArray(),
                    currentAction,
                    currentMaxConf));

                currentUtf16Start = span.Start;
                currentUtf16End = span.Start + span.Length;
                currentScalarStart = span.ScalarStart;
                currentScalarEnd = span.ScalarStart + span.ScalarLength;
                currentWinner = finding.Category;
                currentWinnerPriority = findingPriority;
                currentAction = findingAction;
                currentMaxConf = finding.Confidence;
                mergedCategories = new SortedSet<string>(StringComparer.Ordinal) { currentWinner };
            }
        }

        resolved.Add(new ResolvedTextSpan(
            currentUtf16Start,
            currentUtf16End - currentUtf16Start,
            currentScalarStart,
            currentScalarEnd - currentScalarStart,
            currentWinner,
            mergedCategories.ToArray(),
            currentAction,
            currentMaxConf));

        return resolved;
    }

    /// <summary>
    /// Applies resolved spans to source text using the host token vault (for mask) or irreversible redaction (for redact/secrets),
    /// preserving JSON syntax if the source text was valid JSON.
    /// </summary>
    public static async ValueTask<TextSanitizationResult> SanitizeAsync(
        string content,
        IReadOnlyList<ResolvedTextSpan> resolvedSpans,
        EgressContext context,
        ITokenVault? vault = null,
        TimeSpan? tokenTtl = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(resolvedSpans);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (resolvedSpans.Count == 0)
        {
            return new TextSanitizationResult(
                true,
                content,
                SecurityPolicyAction.Allow,
                resolvedSpans,
                Array.Empty<EntitySpan>(),
                "clean");
        }

        var entitySpans = new List<EntitySpan>(resolvedSpans.Count);
        var strictestAction = SecurityPolicyAction.Allow;
        foreach (var span in resolvedSpans)
        {
            if (GetActionStrictness(span.Action) > GetActionStrictness(strictestAction))
                strictestAction = span.Action;
            foreach (var category in span.MergedCategories)
                entitySpans.Add(new EntitySpan(span.ScalarStart, span.ScalarLength, category));
        }

        if (strictestAction == SecurityPolicyAction.Block)
        {
            return new TextSanitizationResult(false, null, SecurityPolicyAction.Block, resolvedSpans, entitySpans, "policy_blocked");
        }

        if (strictestAction == SecurityPolicyAction.Review)
        {
            return new TextSanitizationResult(false, null, SecurityPolicyAction.Review, resolvedSpans, entitySpans, "review_required");
        }

        var inputIsValidJson = IsValidJson(content);
        var ttl = tokenTtl ?? TimeSpan.FromMinutes(30);
        var genericScope = new PrivacyScope(context.Tenant, context.Principal, context.Session, "redaction");
        var sb = new StringBuilder(content.Length);
        var cursor = 0;

        foreach (var span in resolvedSpans)
        {
            if (span.Action == SecurityPolicyAction.Allow)
                continue;

            var start = span.Utf16Start;
            var end = span.Utf16Start + span.Utf16Length;

            // If input is valid JSON, avoid slicing through structural quote characters.
            if (inputIsValidJson)
            {
                while (start < end && content[start] == '"')
                    start++;
                while (end > start && content[end - 1] == '"')
                    end--;
                if (start >= end)
                    return new TextSanitizationResult(false, null, SecurityPolicyAction.Block, resolvedSpans, entitySpans, "json_syntax_preservation_failed");
            }

            if (start > cursor)
                sb.Append(content, cursor, start - cursor);

            var rawValue = content.Substring(start, end - start);
            string replacement;
            if (span.Action == SecurityPolicyAction.Redact || IsSecretCategory(span.WinningCategory))
            {
                // Irreversible redaction for Redact action and secrets (no secret restoration through generic policies).
                replacement = $"[OC_{span.WinningCategory.ToUpperInvariant()}_REDACTED]";
            }
            else if (vault is not null)
            {
                replacement = await vault.StoreAsync(rawValue, span.WinningCategory, genericScope, ttl, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                replacement = $"[OC_{span.WinningCategory.ToUpperInvariant()}]";
            }

            sb.Append(replacement);
            cursor = end;
        }

        if (cursor < content.Length)
            sb.Append(content, cursor, content.Length - cursor);

        var sanitized = sb.ToString();
        if (inputIsValidJson && !IsValidJson(sanitized))
        {
            // Fail-closed rather than emitting corrupted JSON.
            return new TextSanitizationResult(false, null, SecurityPolicyAction.Block, resolvedSpans, entitySpans, "json_syntax_preservation_failed");
        }

        return new TextSanitizationResult(true, sanitized, strictestAction, resolvedSpans, entitySpans, "sanitized");
    }

    private static SecurityPolicyAction ResolveAction(
        SecurityFinding finding,
        IReadOnlyDictionary<string, SecurityPolicyAction>? categoryPolicies)
    {
        if (categoryPolicies is not null && categoryPolicies.TryGetValue(finding.Category, out var configured))
            return configured;
        if (finding.RecommendedAction.HasValue)
            return finding.RecommendedAction.Value;
        return IsSecretCategory(finding.Category) ? SecurityPolicyAction.Redact : SecurityPolicyAction.Mask;
    }

    private static int GetCategoryPriority(string category)
    {
        if (IsSecretCategory(category))
            return 100;
        return category.ToUpperInvariant() switch
        {
            "PAYMENT_CARD" or "CREDIT_CARD" => 80,
            "SA_ID" or "NATIONAL_ID" or "SSN" or "PASSPORT" or "TAX_ID" or "BANK_ACCOUNT" => 70,
            "EMAIL" or "PHONE" => 50,
            "PERSONAL_DATA" or "PERSON" or "ADDRESS" or "ORGANIZATION" or "DATE" => 30,
            _ => 10
        };
    }

    private static int GetActionStrictness(SecurityPolicyAction action) => action switch
    {
        SecurityPolicyAction.Block => 4,
        SecurityPolicyAction.Review => 3,
        SecurityPolicyAction.Redact => 2,
        SecurityPolicyAction.Mask => 1,
        _ => 0
    };

    private static bool IsValidJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
            return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
