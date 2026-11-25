using OpenConnect.Abstractions;

namespace OpenConnect.Privacy.Worker;

/// <summary>
/// <see cref="ISecurityDetector"/> backed by <see cref="ISecurityWorkerClient"/>.
/// Enforces required detector coverage (no implicit downgrade when semantic coverage is mandatory),
/// validates Unicode scalar offsets before touching content, and applies deterministic host overlap resolution.
/// </summary>
public sealed class SecurityWorkerDetector : ISecurityDetector
{
    private readonly ISecurityWorkerClient _client;
    private readonly IReadOnlyList<string> _detectors;
    private readonly IReadOnlySet<string> _requiredDetectors;
    private readonly string _policyRevision;
    private readonly ITokenVault? _vault;
    private readonly IReadOnlyDictionary<string, SecurityPolicyAction>? _categoryPolicies;

    /// <summary>Creates a new <see cref="SecurityWorkerDetector"/>.</summary>
    public SecurityWorkerDetector(
        ISecurityWorkerClient client,
        IReadOnlyList<string> detectors,
        IReadOnlySet<string>? requiredDetectors = null,
        string policyRevision = "default-1",
        ITokenVault? vault = null,
        IReadOnlyDictionary<string, SecurityPolicyAction>? categoryPolicies = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(detectors);
        if (detectors.Count == 0)
            throw new ArgumentException("At least one detector must be specified.", nameof(detectors));
        _detectors = detectors.ToArray();
        _requiredDetectors = requiredDetectors ?? new HashSet<string>(_detectors, StringComparer.Ordinal);
        _policyRevision = string.IsNullOrWhiteSpace(policyRevision) ? "default-1" : policyRevision;
        _vault = vault;
        _categoryPolicies = categoryPolicies;
    }

    /// <inheritdoc />
    public async ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        if (content.Length == 0)
            return new DetectorResult(true, string.Empty, Array.Empty<EntitySpan>());

        var request = new SecurityTextInspectionRequest(
            Guid.NewGuid().ToString("D"),
            _policyRevision,
            _detectors,
            content,
            _requiredDetectors);

        var response = await _client.InspectTextAsync(request, cancellationToken).ConfigureAwait(false);
        if (response is null || response.Status != SecurityInspectionStatus.Completed)
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());

        // Explicitly verify every required detector completed (no silent downgrade to rules-only).
        foreach (var required in _requiredDetectors)
        {
            var status = response.Detectors.FirstOrDefault(d => string.Equals(d.Id, required, StringComparison.Ordinal));
            if (status is null || status.Status != SecurityInspectionStatus.Completed)
                return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());
        }

        if (!UnicodeScalarOffsetConverter.TryValidateTextFindings(content, response.Findings, out var validated))
            return new DetectorResult(false, string.Empty, Array.Empty<EntitySpan>());

        var resolved = TextOverlapResolver.ResolveOverlaps(validated, _categoryPolicies);
        var sanitized = await TextOverlapResolver.SanitizeAsync(content, resolved, context, _vault, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!sanitized.Allowed || sanitized.SanitizedContent is null)
            return new DetectorResult(false, string.Empty, sanitized.EntitySpans);

        return new DetectorResult(true, sanitized.SanitizedContent, sanitized.EntitySpans);
    }
}
