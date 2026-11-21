using System.Buffers;
using System.Text;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudVoicePrivacyRequest(string Text, string VoiceId, string Locale, string Format);
public sealed record CloudVoicePrivacyResult(bool Allowed, string Code, int Characters = 0);

public interface ICloudVoicePrivacyEvidenceStore
{
    Task AppendAsync(CloudCredentialIdentity identity, Guid requestId, string fingerprintKeyId, byte[] fingerprint,
        int characters, string outcome, string traceId, CancellationToken cancellationToken);
}

// This Cloud screen is independent from the application host's privacy firewall.
public sealed class CloudVoicePrivacyService(ISecurityDetector? detector)
{
    private readonly PrivacyFirewall _firewall = new(Options.Create(new PrivacyOptions()), new PolicyEngine(detector));

    public async Task<CloudVoicePrivacyResult> EvaluateAsync(CloudCredentialIdentity identity, Guid requestId,
        CloudVoicePrivacyRequest request, CancellationToken cancellationToken)
    {
        if (identity is null || identity.TenantId == Guid.Empty || identity.ApplicationId == Guid.Empty || requestId == Guid.Empty ||
            request is null || string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 4000 ||
            request.VoiceId != "af_heart" || string.IsNullOrEmpty(request.Locale) || request.Locale.Length > 32 ||
            !TryCountScalars(request.Locale, out var localeLength) || localeLength > 16 || request.Format is not ("wav" or "pcm") ||
            !TryCountScalars(request.Text, out var characters) || characters is < 1 or > 2000) return new(false, "invalid_request");
        try
        {
            if (!_firewall.Enabled) return new(false, "detector_unavailable", characters);
            var context = new EgressContext(new TenantId(ToUlid(identity.TenantId)), new PrincipalId(ToUlid(identity.ApplicationId)),
                new SessionId(ToUlid(requestId)), EgressDestination.Worker);
            string[] fields = [request.Text, request.VoiceId, request.Locale, request.Format];
            foreach (var content in fields.Append(string.Concat(fields)))
            {
                var decision = await _firewall.EvaluateAsync(content, context, cancellationToken);
                if (!decision.Allowed || decision.DetectedEntities is null || decision.SanitizedContent is null)
                    return new(false, "detector_unavailable", characters);
                if (decision.DetectedEntities.Count != 0 || !string.Equals(content, decision.SanitizedContent, StringComparison.Ordinal))
                    return new(false, "privacy_blocked", characters);
            }
            return new(true, "allowed", characters);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return new(false, "detector_unavailable", characters); }
    }

    private static bool TryCountScalars(string value, out int count)
    {
        count = 0;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done) return false;
            remaining = remaining[consumed..];
            count++;
        }
        return true;
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
