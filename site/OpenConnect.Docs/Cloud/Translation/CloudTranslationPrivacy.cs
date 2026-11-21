using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;
using OpenConnect.Privacy.Policy;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudTranslationPrivacyInput(string Text, string SourceLang, string TargetLang);
public sealed record CloudTranslationPrivacyResult(bool Allowed, string Code, int Characters = 0);

// Cloud-side inspection is independent from any application-host privacy decision.
public sealed partial class CloudTranslationPrivacyService(ISecurityDetector? detector)
{
    private readonly PrivacyFirewall _firewall = new(Options.Create(new PrivacyOptions()), new PolicyEngine(detector));

    public Task<CloudTranslationPrivacyResult> InspectInputAsync(CloudCredentialIdentity identity, Guid requestId,
        CloudTranslationPrivacyInput input, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(identity, requestId) || input is null || !ValidText(input.Text, 5000, out var characters) ||
            !ValidLanguage(input.SourceLang) || !ValidLanguage(input.TargetLang) || input.SourceLang == input.TargetLang)
            return Task.FromResult(new CloudTranslationPrivacyResult(false, "invalid_request"));
        string[] fields = [input.Text, input.SourceLang, input.TargetLang];
        return InspectAsync(identity, requestId, fields.Append(string.Concat(fields)), EgressDestination.Worker, characters, cancellationToken);
    }

    public Task<CloudTranslationPrivacyResult> InspectOutputAsync(CloudCredentialIdentity identity, Guid requestId,
        string output, CancellationToken cancellationToken)
    {
        if (!ValidIdentity(identity, requestId) || !ValidText(output, 20000, out var characters))
            return Task.FromResult(new CloudTranslationPrivacyResult(false, "invalid_request"));
        return InspectAsync(identity, requestId, [output], EgressDestination.Output, characters, cancellationToken);
    }

    private async Task<CloudTranslationPrivacyResult> InspectAsync(CloudCredentialIdentity identity, Guid requestId,
        IEnumerable<string> fields, EgressDestination destination, int characters, CancellationToken cancellationToken)
    {
        try
        {
            if (!_firewall.Enabled) return new(false, "detector_unavailable", characters);
            var context = new EgressContext(new TenantId(ToUlid(identity.TenantId)), new PrincipalId(ToUlid(identity.ApplicationId)),
                new SessionId(ToUlid(requestId)), destination);
            foreach (var content in fields)
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

    private static bool ValidIdentity(CloudCredentialIdentity? identity, Guid requestId) =>
        identity is not null && identity.TenantId != Guid.Empty && identity.ApplicationId != Guid.Empty && requestId != Guid.Empty;

    private static bool ValidText(string? value, int maximum, out int count)
    {
        count = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out _, out var consumed) != OperationStatus.Done || ++count > maximum) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }

    private static bool ValidLanguage(string? value) => value is not null && LanguagePattern().IsMatch(value);

    [GeneratedRegex("\\A[a-z]{3}_[A-Z][a-z]{3}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex LanguagePattern();

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
