using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenConnect.Abstractions;

namespace OpenConnect.Privacy;

/// <summary>Durably records content-free evidence before a privacy decision is returned.</summary>
public interface IHostPrivacyEvidenceSink
{
    /// <summary>Persists or durably queues an event; retry by event ID must be idempotent.</summary>
    ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default);
}

/// <summary>Opt-in firewall wrapper requiring successful evidence recording for each decision.</summary>
public sealed class EvidenceRecordingPrivacyFirewall : IPrivacyFirewall
{
    private readonly IPrivacyFirewall _inner;
    private readonly IHostPrivacyEvidenceSink _sink;

    /// <summary>Creates a recording wrapper around the host firewall and durable evidence sink.</summary>
    public EvidenceRecordingPrivacyFirewall(IPrivacyFirewall inner, IHostPrivacyEvidenceSink sink)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }

    /// <inheritdoc />
    public bool Enabled => _inner.Enabled;

    /// <summary>
    /// Checks enforcement with empty input. Successful probes are not recorded as content decisions;
    /// failed probes are recorded before returning false.
    /// </summary>
    public async ValueTask<bool> CheckReadinessAsync(EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EgressDecision decision;
        try
        {
            decision = _inner.Enabled
                ? await _inner.EvaluateAsync(string.Empty, context, cancellationToken).ConfigureAwait(false)
                : new EgressDecision(false, null, [], SecurityPolicyAction.Block, "privacy_enforcement_unavailable");
            if (decision is null)
                throw new InvalidOperationException();
            if (!_inner.Enabled)
                decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "privacy_enforcement_unavailable");
            else if (decision.Reason == "privacy_enforcement_disabled")
                decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "privacy_enforcement_unavailable");
            else if (decision.Allowed && decision.Action == SecurityPolicyAction.Allow && decision.SanitizedContent == string.Empty &&
                decision.DetectedEntities is { Count: 0 })
                return true;
            else if (decision.Allowed || decision.DetectedEntities is null || decision.DetectedEntities.Count != 0 ||
                !Enum.IsDefined(decision.Action))
                decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure"); }
        try
        { await _sink.RecordAsync(HostPrivacyEvidence.Create(Guid.NewGuid(), context, decision), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new PrivacyBlockedException("Privacy evidence recording failed."); }
        return false;
    }

    /// <inheritdoc />
    public async ValueTask<EgressDecision> EvaluateAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        EgressDecision decision;
        try
        {
            decision = _inner.Enabled
                ? await _inner.EvaluateAsync(content, context, cancellationToken).ConfigureAwait(false)
                : new EgressDecision(false, null, [], SecurityPolicyAction.Block, "privacy_enforcement_unavailable");
            if (decision is null)
                throw new InvalidOperationException();
            if (!_inner.Enabled)
                decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "privacy_enforcement_unavailable");
            else if (decision.Reason == "privacy_enforcement_disabled")
                decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "privacy_enforcement_unavailable");
            else if (decision.Allowed && (!Enum.IsDefined(decision.Action) || decision.Action is SecurityPolicyAction.Block or SecurityPolicyAction.Review ||
                decision.Action == SecurityPolicyAction.Allow && decision.DetectedEntities?.Count > 0 ||
                !IsSanitized(content, decision)))
                decision = new EgressDecision(false, null, decision.DetectedEntities?.Where(entity => entity is not null)
                    .Take(20000).ToArray() ?? [], SecurityPolicyAction.Block, "detector_failure");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { decision = new EgressDecision(false, null, [], SecurityPolicyAction.Block, "detector_failure"); }
        try
        {
            var evidence = HostPrivacyEvidence.Create(Guid.NewGuid(), context, decision);
            await _sink.RecordAsync(evidence, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new PrivacyBlockedException("Privacy evidence recording failed."); }
        return decision;
    }

    private static bool IsSanitized(string source, EgressDecision decision)
    {
        if (decision.SanitizedContent is null || decision.DetectedEntities is null)
            return false;
        var entities = decision.DetectedEntities;
        if (entities.Count == 0)
            return true;
        if (entities.Count > 20000 || (long)entities.Count * decision.SanitizedContent.Length > 100_000_000 ||
            source == decision.SanitizedContent)
            return false;
        var offsets = new List<int>();
        var offset = 0;
        foreach (var rune in source.EnumerateRunes())
        { offsets.Add(offset); offset += rune.Utf16SequenceLength; }
        offsets.Add(offset);
        foreach (var entity in entities)
        {
            if (entity is null || entity.Start < 0 || entity.Length <= 0 || (long)entity.Start + entity.Length >= offsets.Count)
                return false;
            var start = offsets[entity.Start];
            if (decision.SanitizedContent.AsSpan().Contains(source.AsSpan(start, offsets[entity.Start + entity.Length] - start),
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    internal async Task RecordBoundaryBlockAsync(EgressContext context, IReadOnlyList<EntitySpan> entities,
        CancellationToken cancellationToken)
    {
        var decision = new EgressDecision(false, null, entities, SecurityPolicyAction.Block, "sensitive_boundary_blocked");
        try
        { await _sink.RecordAsync(HostPrivacyEvidence.Create(Guid.NewGuid(), context, decision), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new PrivacyBlockedException("Privacy evidence recording failed."); }
    }

    internal async Task DemandUnchangedBatchAsync(IEnumerable<PrivacyJsonInspection.Fragment> fragments,
        IEnumerable<PrivacyJsonInspection.Fragment> contextualFields, IEnumerable<PrivacyJsonInspection.Fragment> joinedValues,
        EgressContext context, CancellationToken cancellationToken, int maximumFragments = PrivacyJsonInspection.MaximumFragments)
    {
        var variants = new Dictionary<(int Group, int Source, string Category), EntitySpan[]>();
        static string Category(EntitySpan entity) => entity.EntityType is "EMAIL" or "PHONE" or "PAYMENT_CARD" or "SECRET" or "PERSONAL_DATA"
            ? entity.EntityType : "OTHER";
        var allowed = true;
        var inspectionFailed = false;
        var enforcementUnavailable = false;
        var fragmentCount = 0;
        var storedEntities = 0;
        try
        {
            foreach (var (item, group) in fragments.Select(item => (item, 0))
                .Concat(contextualFields.Select(item => (item, 1)))
                .Concat(joinedValues.Select(item => (item, 2))))
            {
                var fragment = item.Text;
                cancellationToken.ThrowIfCancellationRequested();
                if (!_inner.Enabled)
                { allowed = false; enforcementUnavailable = true; break; }
                if (++fragmentCount > maximumFragments)
                { allowed = false; inspectionFailed = true; break; }
                var decision = await _inner.EvaluateAsync(fragment, context, cancellationToken).ConfigureAwait(false);
                var scalarLength = decision?.DetectedEntities is { Count: > 0 } ? fragment.EnumerateRunes().Count() : 0;
                if (decision?.Reason == "privacy_enforcement_disabled")
                { allowed = false; enforcementUnavailable = true; break; }
                if (decision is null || decision.DetectedEntities is null || decision.DetectedEntities.Count > 20000 ||
                    !Enum.IsDefined(decision.Action) || decision.Allowed && (decision.SanitizedContent is null || decision.Action == SecurityPolicyAction.Block) ||
                    decision.Allowed && decision.Action == SecurityPolicyAction.Review ||
                    decision.DetectedEntities.Any(entity => entity is null || entity.Start < 0 || entity.Length <= 0 ||
                        (long)entity.Start + entity.Length > scalarLength))
                { allowed = false; inspectionFailed = true; break; }
                foreach (var category in decision.DetectedEntities.GroupBy(Category))
                {
                    var key = (group == 0 && item.Source == -1 ? -1 : group, item.Source, category.Key);
                    var detected = category.ToArray();
                    if (!variants.TryGetValue(key, out var previous) || detected.Length > previous.Length)
                    {
                        storedEntities += detected.Length - (previous?.Length ?? 0);
                        if (storedEntities > 80000)
                            throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
                        variants[key] = detected;
                    }
                }
                if (!decision.Allowed && decision.Reason == "detector_failure")
                    inspectionFailed = true;
                if (!decision.Allowed && decision.Reason is ("required_detector_unavailable" or "privacy_enforcement_unavailable"))
                    enforcementUnavailable = true;
                if (!decision.Allowed || decision.Action == SecurityPolicyAction.Block ||
                    decision.DetectedEntities.Count != 0 || decision.SanitizedContent != fragment)
                    allowed = false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!_inner.Enabled)
            { allowed = false; enforcementUnavailable = true; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { allowed = false; inspectionFailed = true; }
        // Raw JSON, decoded values and contextual fields overlap. Keep the largest count per category
        // rather than reporting the same match twice. Counts remain detection evidence,
        // not a guarantee of a complete or unique inventory of sensitive values.
        var combined = variants.GroupBy(entry => entry.Key.Category).SelectMany(category =>
            category.GroupBy(entry => entry.Key.Group)
                .Select(group => group.SelectMany(entry => entry.Value).Take(20001).ToArray())
                .MaxBy(group => group.Length) ?? []).Take(20001).ToArray();
        if (combined.Length > 20000)
        { allowed = false; inspectionFailed = true; combined = combined[..20000]; }
        var summary = new EgressDecision(allowed, allowed ? string.Empty : null, combined,
            allowed ? SecurityPolicyAction.Allow : SecurityPolicyAction.Block,
            enforcementUnavailable ? "privacy_enforcement_unavailable" : inspectionFailed ? "detector_failure" : "structured_content_inspection");
        try
        { await _sink.RecordAsync(HostPrivacyEvidence.Create(Guid.NewGuid(), context, summary), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new PrivacyBlockedException("Privacy evidence recording failed."); }
        if (!allowed)
            throw new PrivacyBlockedException("Structured content was blocked by privacy policy.");
    }
}

/// <summary>Registers an explicitly configured evidence-recording firewall.</summary>
public static class EvidenceRecordingPrivacyServiceCollectionExtensions
{
    /// <summary>
    /// Replaces the host firewall registration. The supplied factory must create the inner firewall
    /// without resolving <see cref="IPrivacyFirewall"/>; a durable sink must also be registered.
    /// </summary>
    public static IServiceCollection AddRecordedPrivacyFirewall(this IServiceCollection services,
        Func<IServiceProvider, IPrivacyFirewall> innerFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(innerFactory);
        services.RemoveAll<IPrivacyFirewall>();
        services.AddSingleton<IPrivacyFirewall>(provider => new EvidenceRecordingPrivacyFirewall(
            innerFactory(provider), provider.GetRequiredService<IHostPrivacyEvidenceSink>()));
        return services;
    }
}
