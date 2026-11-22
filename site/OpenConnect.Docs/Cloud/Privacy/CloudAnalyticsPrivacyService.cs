using System.Text.Json;
using OpenConnect.Privacy;

namespace OpenConnect.Docs.Cloud;

/// <summary>Applies the shared JSON privacy boundary to analytics payloads.</summary>
public sealed class CloudAnalyticsPrivacyService(ISecurityDetector? detector)
{
    private readonly CloudJsonPrivacyService _privacy = new(detector);

    public Task<CloudPrivacyDecision> EvaluateAsync(CloudCredentialIdentity identity, Guid requestId,
        JsonElement request, CancellationToken cancellationToken) =>
        _privacy.EvaluateAsync(identity, requestId, request, cancellationToken);

    public Task<CloudPrivacyAssessment> EvaluateWithEvidenceAsync(CloudCredentialIdentity identity, Guid requestId,
        JsonElement request, CancellationToken cancellationToken) =>
        _privacy.EvaluateWithEvidenceAsync(identity, requestId, request, cancellationToken);
}
