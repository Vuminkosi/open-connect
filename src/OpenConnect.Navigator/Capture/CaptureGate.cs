namespace OpenConnect.Navigator.Capture;

/// <summary>Scoped, expiring consent for a single capture artifact and purpose.</summary>
public sealed record NavigatorCaptureConsent(
    string Scope,
    string ArtifactId,
    string Purpose,
    DateTimeOffset ExpiresAt);

/// <summary>Evidence that sensitive pixels were removed from the artifact itself.</summary>
public sealed record NavigatorPixelRedactionProof(bool PixelsRedacted, bool ServerReinspectionPassed);

public sealed record NavigatorCaptureDecision(bool Allowed, DateTimeOffset? ExpiresAt, string Reason)
{
    public static NavigatorCaptureDecision Deny(string reason) => new(false, null, reason);
}

/// <summary>Fail-closed policy gate for capture. This does not persist artifacts or grant mutation approval.</summary>
public sealed class CaptureGate
{
    public NavigatorCaptureDecision Evaluate(
        NavigatorOptions options,
        NavigatorCaptureMode mode,
        string scope,
        string artifactId,
        string purpose,
        NavigatorCaptureConsent? consent,
        string sensitivity,
        bool fieldsAreAmbiguous,
        NavigatorPixelRedactionProof? redactionProof,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(mode) || mode == NavigatorCaptureMode.None || !options.Capture.Enabled ||
            !options.Capture.AllowedModes.Contains(mode))
            return NavigatorCaptureDecision.Deny("capture_disabled");

        if (string.IsNullOrWhiteSpace(scope) || string.IsNullOrWhiteSpace(artifactId) || string.IsNullOrWhiteSpace(purpose))
            return NavigatorCaptureDecision.Deny("invalid_capture_scope");

        if (fieldsAreAmbiguous || !string.Equals(sensitivity, "public", StringComparison.OrdinalIgnoreCase))
            return NavigatorCaptureDecision.Deny("sensitive_or_ambiguous_content");

        if (consent is null || !string.Equals(consent.Scope, scope, StringComparison.Ordinal) ||
            !string.Equals(consent.ArtifactId, artifactId, StringComparison.Ordinal) ||
            !string.Equals(consent.Purpose, purpose, StringComparison.Ordinal) || consent.ExpiresAt <= utcNow)
            return NavigatorCaptureDecision.Deny("consent_missing_or_expired");

        if (options.Capture.ArtifactTtl <= TimeSpan.Zero)
            return NavigatorCaptureDecision.Deny("invalid_artifact_ttl");

        if (mode == NavigatorCaptureMode.MaskedScreenshot &&
            (redactionProof is not { PixelsRedacted: true, ServerReinspectionPassed: true }))
            return NavigatorCaptureDecision.Deny("redaction_unverified");

        var expiresAt = utcNow + options.Capture.ArtifactTtl;
        if (consent.ExpiresAt < expiresAt) expiresAt = consent.ExpiresAt;
        return expiresAt <= utcNow
            ? NavigatorCaptureDecision.Deny("consent_expired")
            : new NavigatorCaptureDecision(true, expiresAt, string.Empty);
    }
}
