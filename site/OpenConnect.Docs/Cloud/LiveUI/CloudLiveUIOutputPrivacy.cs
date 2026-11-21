using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Docs.Cloud;

// Model output is independently inspected before it crosses the Cloud boundary.
public sealed class CloudLiveUIOutputPrivacyService(ISecurityDetector? detector)
{
    private readonly PrivacyFirewall _firewall = new(Options.Create(new PrivacyOptions()), new PolicyEngine(detector));

    public async Task<CloudPrivacyDecision> EvaluateAsync(CloudCredentialIdentity identity, Guid requestId, IReadOnlyList<string> fields, CancellationToken cancellationToken)
    {
        if (identity is null || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            fields is null || fields.Count is < 3 or > 97 || fields.Any(x => x is null || x.Length > 512) ||
            fields.Sum(x => (long)x.Length) is < 1 or > 131072)
            return new(false, "invalid_request");
        try
        {
            if (!_firewall.Enabled) return new(false, "detector_unavailable");
            var context = new EgressContext(new TenantId(ToUlid(identity.TenantId)), new PrincipalId(ToUlid(identity.ApplicationId)),
                new SessionId(ToUlid(requestId)), EgressDestination.Output);
            foreach (var output in fields.Append(string.Concat(fields)))
            {
                if (output.Length == 0) continue;
                var decision = await _firewall.EvaluateAsync(output, context, cancellationToken);
                if (!decision.Allowed || decision.DetectedEntities is null || decision.SanitizedContent is null)
                    return new(false, "detector_unavailable");
                if (decision.DetectedEntities.Count != 0 || !string.Equals(output, decision.SanitizedContent, StringComparison.Ordinal))
                    return new(false, "privacy_blocked");
            }
            return new(true, "allowed");
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
