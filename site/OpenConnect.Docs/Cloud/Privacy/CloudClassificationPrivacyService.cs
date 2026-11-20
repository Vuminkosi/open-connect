using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudPrivacyDecision(bool Allowed, string Code);

// This is an independent Cloud boundary, not evidence of an application-host decision.
public sealed class CloudClassificationPrivacyService(ISecurityDetector? detector)
{
    private readonly PrivacyFirewall _firewall = new(Options.Create(new PrivacyOptions()), new PolicyEngine(detector));

    public async Task<CloudPrivacyDecision> EvaluateAsync(CloudCredentialIdentity identity, Guid requestId, string content, CancellationToken cancellationToken)
    {
        if (identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty || string.IsNullOrEmpty(content))
            return new(false, "invalid_request");
        try
        {
            if (!_firewall.Enabled) return new(false, "detector_unavailable");
            var context = new EgressContext(new TenantId(ToUlid(identity.TenantId)), new PrincipalId(ToUlid(identity.ApplicationId)),
                new SessionId(ToUlid(requestId)), EgressDestination.Worker);
            var decision = await _firewall.EvaluateAsync(content, context, cancellationToken);
            if (!decision.Allowed || decision.DetectedEntities is null || decision.SanitizedContent is null)
                return new(false, "detector_unavailable");
            // Until a scoped durable redaction vault and receipt are available, block rather than forward transformed data.
            return decision.DetectedEntities.Count == 0 && string.Equals(content, decision.SanitizedContent, StringComparison.Ordinal)
                ? new(true, "allowed") : new(false, "privacy_blocked");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, "detector_unavailable"); }
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
