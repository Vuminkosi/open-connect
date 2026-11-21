using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Docs.Cloud;

/// <summary>Inspects decoded JSON independently of the feature using the privacy boundary.</summary>
public sealed class CloudJsonPrivacyService(ISecurityDetector? detector)
{
    public async Task<CloudPrivacyDecision> EvaluateAsync(CloudCredentialIdentity identity, Guid requestId,
        JsonElement request, CancellationToken cancellationToken) =>
        (await EvaluateWithEvidenceAsync(identity, requestId, request, cancellationToken)).Decision;

    public async Task<CloudPrivacyAssessment> EvaluateWithEvidenceAsync(CloudCredentialIdentity identity, Guid requestId,
        JsonElement request, CancellationToken cancellationToken, bool embeddingResponse = false)
    {
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            request.ValueKind != JsonValueKind.Object) return new(new(false, "invalid_request"), []);
        if (detector is null) return new(new(false, "detector_unavailable"), []);
        var recording = new EvidenceDetector(detector);
        var firewall = new PrivacyFirewall(Options.Create(new PrivacyOptions()), new PolicyEngine(recording));
        try
        {
            var context = new EgressContext(new TenantId(ToUlid(identity.TenantId)), new PrincipalId(ToUlid(identity.ApplicationId)),
                new SessionId(ToUlid(requestId)), EgressDestination.Worker);
            if (embeddingResponse)
                await PrivacyJsonInspection.DemandEmbeddingResponseUnchangedAsync(firewall, request, context, cancellationToken);
            else
                await PrivacyJsonInspection.DemandUnchangedAsync(firewall, request, context, cancellationToken);
            return new(new(true, "allowed"), []);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PrivacyBlockedException)
        {
            return recording.Unavailable ? new(new(false, "detector_unavailable"), []) :
                new(new(false, "privacy_blocked"), recording.Categories());
        }
        catch { return new(new(false, "detector_unavailable"), []); }
    }

    private sealed class EvidenceDetector(ISecurityDetector inner) : ISecurityDetector
    {
        private readonly List<EntitySpan> _entities = [];
        public bool Unavailable { get; private set; }
        public async ValueTask<DetectorResult> DetectAndSanitizeAsync(string content, EgressContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await inner.DetectAndSanitizeAsync(content, context, cancellationToken);
                if (result is null || !result.Available || result.DetectedEntities is null) { Unavailable = true; return result!; }
                _entities.AddRange(result.DetectedEntities);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { Unavailable = true; throw; }
        }

        public IReadOnlyList<CloudPrivacyCategoryCount> Categories() => _entities
            .GroupBy(entity => entity.EntityType switch
            { "EMAIL" or "PHONE" or "PAYMENT_CARD" or "SECRET" or "PERSONAL_DATA" => entity.EntityType, _ => "OTHER" }, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => new CloudPrivacyCategoryCount(group.Key, group.Count())).ToArray();
    }

    private static Ulid ToUlid(Guid value)
    {
        UInt128 number = 0;
        foreach (var b in value.ToByteArray(bigEndian: true)) number = (number << 8) | b;
        Span<char> encoded = stackalloc char[26];
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        for (var i = encoded.Length - 1; i >= 0; i--) { encoded[i] = alphabet[(int)(number & 31)]; number >>= 5; }
        return Ulid.Parse(new string(encoded));
    }
}
