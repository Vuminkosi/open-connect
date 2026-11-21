using System.Text;
using System.Text.Json;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Gates;

namespace OpenConnect.Privacy;

/// <summary>Inspects JSON and its decoded strings without changing its structure or citations.</summary>
public static class PrivacyJsonInspection
{
    internal const int MaximumFragments = 50000;
    /// <summary>Records a caller-enforced block when detected entities cross a protected content boundary.</summary>
    public static async Task RecordBoundaryBlockAsync(IEgressGate gate, EgressContext context,
        IReadOnlyList<EntitySpan> entities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entities);
        if (entities.Count == 0 || entities.Count > 20000 || entities.Any(entity => entity is null))
            throw new ArgumentException("Invalid boundary evidence.", nameof(entities));
        if (gate is EgressGate { Firewall: EvidenceRecordingPrivacyFirewall recording })
            await recording.RecordBoundaryBlockAsync(context, entities, cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Inspects through an egress gate, preserving batched recording for the standard host gate.</summary>
    public static async Task DemandGateUnchangedAsync(IEgressGate gate, JsonElement value, EgressContext context,
        CancellationToken cancellationToken = default)
        => await DemandGateUnchangedCoreAsync(gate, value, context, MaximumFragments, 1_000_000, cancellationToken).ConfigureAwait(false);

    /// <summary>Inspects a bounded embedding response through the host egress gate.</summary>
    public static async Task DemandGateEmbeddingResponseUnchangedAsync(IEgressGate gate, JsonElement value, EgressContext context,
        CancellationToken cancellationToken = default)
        => await DemandGateUnchangedCoreAsync(gate, value, context, 2_000_000, 16_000_000, cancellationToken).ConfigureAwait(false);

    private static async Task DemandGateUnchangedCoreAsync(IEgressGate gate, JsonElement value, EgressContext context,
        int maximumFragments, int maximumCombinedLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(context);
        if (gate is EgressGate standard)
        {
            await DemandUnchangedCoreAsync(standard.Firewall, value, context, maximumFragments, maximumCombinedLength, cancellationToken).ConfigureAwait(false);
            return;
        }
        try
        {
            var count = 0;
            foreach (var item in Fragments(value).Concat(Expand(ContextualFields(value))).Concat(Expand(CombinedValues(value, maximumFragments, maximumCombinedLength))))
            {
                var fragment = item.Text;
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > maximumFragments) throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
                var decision = await gate.EvaluateAsync(fragment, context, cancellationToken).ConfigureAwait(false);
                if (decision is null || !decision.Allowed || !Enum.IsDefined(decision.Action) || decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
                    decision.Reason == "privacy_enforcement_disabled" || decision.DetectedEntities is null ||
                    decision.DetectedEntities.Count != 0 || decision.SanitizedContent != fragment)
                    throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new PrivacyBlockedException("Structured content was blocked by privacy policy."); }
    }
    /// <summary>Blocks if any inspected fragment is denied, altered, or cannot be inspected.</summary>
    public static async Task DemandUnchangedAsync(IPrivacyFirewall firewall, JsonElement value, EgressContext context,
        CancellationToken cancellationToken = default)
        => await DemandUnchangedCoreAsync(firewall, value, context, MaximumFragments, 1_000_000, cancellationToken).ConfigureAwait(false);

    /// <summary>Inspects a bounded embedding response with a budget sized for numeric vectors.</summary>
    public static async Task DemandEmbeddingResponseUnchangedAsync(IPrivacyFirewall firewall, JsonElement value, EgressContext context,
        CancellationToken cancellationToken = default)
        => await DemandUnchangedCoreAsync(firewall, value, context, 2_000_000, 16_000_000, cancellationToken).ConfigureAwait(false);

    private static async Task DemandUnchangedCoreAsync(IPrivacyFirewall firewall, JsonElement value, EgressContext context,
        int maximumFragments, int maximumCombinedLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(firewall);
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var fragments = Fragments(value);
            if (firewall is EvidenceRecordingPrivacyFirewall recording)
            {
                await recording.DemandUnchangedBatchAsync(fragments, Expand(ContextualFields(value)), Expand(CombinedValues(value, maximumFragments, maximumCombinedLength)), context, cancellationToken, maximumFragments).ConfigureAwait(false);
                return;
            }
            if (!firewall.Enabled) throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
            var count = 0;
            foreach (var item in fragments.Concat(Expand(ContextualFields(value))).Concat(Expand(CombinedValues(value, maximumFragments, maximumCombinedLength))))
            {
                var fragment = item.Text;
                cancellationToken.ThrowIfCancellationRequested();
                if (!firewall.Enabled || ++count > maximumFragments) throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
                var decision = await firewall.EvaluateAsync(fragment, context, cancellationToken).ConfigureAwait(false);
                if (decision is null || !decision.Allowed || !Enum.IsDefined(decision.Action) || decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
                    decision.Reason == "privacy_enforcement_disabled" || decision.DetectedEntities is null ||
                    decision.DetectedEntities.Count != 0 || decision.SanitizedContent != fragment)
                    throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!firewall.Enabled) throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new PrivacyBlockedException("Structured content was blocked by privacy policy."); }
    }

    internal readonly record struct Fragment(string Text, int Source);

    private static IEnumerable<Fragment> Expand(IEnumerable<string> fragments)
    {
        var source = 0;
        foreach (var fragment in fragments)
        {
            var index = source++;
            yield return new Fragment(fragment, index);
            var current = fragment;
            // Inspect bounded URL-encoded variants, including one nested encoding.
            for (var depth = 0; depth < 2 && current.Contains('%'); depth++)
            {
                if (current.Length > 1_000_000)
                    throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
                var decoded = Uri.UnescapeDataString(current);
                if (decoded == current) break;
                yield return new Fragment(decoded, index);
                current = decoded;
            }
            // More encoding layers remain uninspected. Fail closed rather than
            // allowing a value that may decode to sensitive data later.
            if (current.Contains('%') && Uri.UnescapeDataString(current) != current)
                throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
        }
    }

    private static IEnumerable<Fragment> Fragments(JsonElement value)
    {
        yield return new Fragment(value.GetRawText(), -1);
        foreach (var text in Expand(Strings(value))) yield return text;
    }

    private static IEnumerable<string> CombinedValues(JsonElement value, int maximumFragments = MaximumFragments, int maximumLength = 1_000_000)
    {
        var combined = new StringBuilder();
        var count = 0;
        foreach (var text in Values(value))
        {
            if (++count > maximumFragments || (long)combined.Length + text.Length > maximumLength)
                throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
            combined.Append(text);
        }
        if (count > 1) yield return combined.ToString();
    }

    private static IEnumerable<string> Values(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Number) yield return value.GetRawText();
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                foreach (var text in Values(item)) yield return text;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
                foreach (var text in Values(property.Value)) yield return text;
    }

    // Field labels and values must be inspected together after JSON escapes are decoded.
    private static IEnumerable<string> ContextualFields(JsonElement value, IReadOnlyList<string>? labels = null)
    {
        if (value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
            if (labels is not null)
                foreach (var label in labels) yield return $"{label}: {text}";
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                foreach (var field in ContextualFields(item, labels)) yield return field;
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                var nextLabels = labels is null ? [property.Name] : labels.Append(property.Name).ToArray();
                foreach (var field in ContextualFields(property.Value, nextLabels)) yield return field;
            }
        }
    }

    private static IEnumerable<string> Strings(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) yield return value.GetString()!;
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray())
                foreach (var text in Strings(item)) yield return text;
        else if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                yield return property.Name;
                foreach (var text in Strings(property.Value)) yield return text;
            }
    }
}
