using OpenConnect.Navigator;
using OpenConnect.Navigator.Capture;
using Xunit;

namespace OpenConnect.Navigator.Tests.Capture;

public sealed class CaptureGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Denies_when_capture_is_disabled_by_default()
    {
        var result = new CaptureGate().Evaluate(new NavigatorOptions(), NavigatorCaptureMode.Semantic,
            "tenant/user", "artifact", "support", Consent(), "public", false, null, Now);
        Assert.False(result.Allowed);
        Assert.Equal("capture_disabled", result.Reason);
    }

    [Theory]
    [InlineData("sensitive", false)]
    [InlineData("public", true)]
    public void Denies_sensitive_or_ambiguous_fields(string sensitivity, bool ambiguous)
    {
        var result = new CaptureGate().Evaluate(Enabled(), NavigatorCaptureMode.Semantic,
            "tenant/user", "artifact", "support", Consent(), sensitivity, ambiguous, null, Now);
        Assert.False(result.Allowed);
        Assert.Equal("sensitive_or_ambiguous_content", result.Reason);
    }

    [Fact]
    public void Screenshot_requires_actual_redaction_and_server_reinspection()
    {
        var gate = new CaptureGate();
        var options = Enabled(NavigatorCaptureMode.MaskedScreenshot);
        Assert.Equal("redaction_unverified", gate.Evaluate(options, NavigatorCaptureMode.MaskedScreenshot,
            "tenant/user", "artifact", "support", Consent(), "public", false,
            new NavigatorPixelRedactionProof(false, true), Now).Reason);
        Assert.True(gate.Evaluate(options, NavigatorCaptureMode.MaskedScreenshot,
            "tenant/user", "artifact", "support", Consent(), "public", false,
            new NavigatorPixelRedactionProof(true, true), Now).Allowed);
    }

    [Fact]
    public void Requires_matching_unexpired_consent_and_caps_ttl()
    {
        var gate = new CaptureGate();
        var options = Enabled();
        Assert.False(gate.Evaluate(options, NavigatorCaptureMode.Semantic, "other", "artifact", "support",
            Consent(), "public", false, null, Now).Allowed);
        var expiringConsent = Consent() with { ExpiresAt = Now.AddMinutes(3) };
        var result = gate.Evaluate(options, NavigatorCaptureMode.Semantic, "tenant/user", "artifact", "support",
            expiringConsent, "public", false, null, Now);
        Assert.True(result.Allowed);
        Assert.Equal(expiringConsent.ExpiresAt, result.ExpiresAt);
        Assert.False(gate.Evaluate(options, NavigatorCaptureMode.Semantic, "tenant/user", "artifact", "support",
            Consent() with { ExpiresAt = Now }, "public", false, null, Now).Allowed);
    }

    private static NavigatorCaptureConsent Consent() => new("tenant/user", "artifact", "support", Now.AddHours(2));

    private static NavigatorOptions Enabled(NavigatorCaptureMode mode = NavigatorCaptureMode.Semantic) => new()
    {
        Capture = new NavigatorCaptureOptions { Enabled = true, AllowedModes = [mode], ArtifactTtl = TimeSpan.FromHours(1) }
    };
}
