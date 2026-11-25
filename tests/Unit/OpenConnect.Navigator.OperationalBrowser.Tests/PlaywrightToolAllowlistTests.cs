using System.Text.Json;
using Xunit;
using OpenConnect.Navigator;
using OpenConnect.Navigator.Playwright;

namespace OpenConnect.Navigator.Playwright.Tests;

public sealed class PlaywrightToolAllowlistTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Observe_MapsToSnapshot()
    {
        Assert.True(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Observe, Args("{}"), null, false, out var tool, out _));
        Assert.Equal("browser_snapshot", tool);
    }

    [Theory]
    [InlineData(NavigatorActionType.Explain)]
    [InlineData(NavigatorActionType.InvokeRegisteredAction)]
    [InlineData(NavigatorActionType.Speak)]
    public void UnsupportedActionsAreDenied(NavigatorActionType action)
    {
        Assert.False(PlaywrightToolAllowlist.TryResolve(action, Args("{}"), null, false, out var tool, out _));
        Assert.Empty(tool);
    }

    [Theory]
    [InlineData("https://evil.example/path")]
    [InlineData("javascript:alert(1)")]
    public void NavigationOutsideApprovedOriginIsDenied(string url)
    {
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Navigate,
            Args(JsonSerializer.Serialize(new { url })), new Uri("https://safe.example"), false, out _, out _));
    }

    [Fact]
    public void CredentialFillAndUnscopedClickAreDenied()
    {
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Fill, Args("{\"target\":\"email\",\"password\":\"x\"}"), null, false, out _, out _, out var fillDenial));
        Assert.Equal("invalid_arguments", fillDenial);
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Click, Args("{}"), null, false, out _, out _, out var clickDenial));
        Assert.Equal("explicit_target_required", clickDenial);
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("secret")]
    [InlineData("API token")]
    [InlineData("Private key")]
    [InlineData("api_key")]
    [InlineData("One-time code")]
    [InlineData("apiKey")]
    [InlineData("Enter OTP")]
    [InlineData("  CVV  ")]
    [InlineData("Passphrase")]
    [InlineData("Pass phrase")]
    [InlineData("Verification code")]
    [InlineData("Auth code")]
    [InlineData("2FA code")]
    [InlineData("MFA code")]
    [InlineData("Recovery code")]
    [InlineData("Credit card")]
    [InlineData("Design tokens")]
    [InlineData("Name\nPassword")]
    [InlineData("Pass\u200Bword")]
    public void FillWithCredentialLikeTargetIsDenied(string target)
    {
        var json = JsonSerializer.Serialize(new { target, value = "x" });
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Fill, Args(json), null, false, out _, out _, out var denial));
        Assert.Equal("sensitive_input_denied", denial);
    }

    [Fact]
    public void FillWithCredentialLikeTargetIsDeniedThroughShortOverload()
    {
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Fill, Args("{\"target\":\"Passphrase\",\"value\":\"x\"}"), null, false, out var tool, out var denial));
        Assert.Equal(string.Empty, tool);
        Assert.Equal("sensitive_input_denied", denial);
    }

    [Fact]
    public void FillWithBenignTargetResolvesAndSelectIsUnaffected()
    {
        Assert.True(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Fill, Args("{\"target\":\"Email\",\"value\":\"a@b.c\"}"), null, false, out var tool, out _, out var denial));
        Assert.Equal("browser_type", tool);
        Assert.Equal(string.Empty, denial);
        Assert.True(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Fill, Args("{\"target\":\"Carbon footprint\",\"value\":\"1\"}"), null, false, out _, out _, out _));
        Assert.True(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Select, Args("{\"target\":\"Password hint\",\"value\":\"x\"}"), null, false, out _, out _, out _));
    }

    [Fact]
    public void ScreenshotRequiresConsentAndWaitIsBounded()
    {
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Capture, Args("{}"), null, false, out _, out _));
        Assert.False(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Scroll, Args("{\"timeout\":60000}"), null, false, out _, out _));
    }

    [Theory]
    [InlineData(NavigatorActionType.Observe, "{\"extra\":true}")]
    [InlineData(NavigatorActionType.Navigate, "{\"url\":\"https://safe.example\",\"extra\":true}")]
    [InlineData(NavigatorActionType.Click, "{\"target\":\"button\",\"extra\":true}")]
    [InlineData(NavigatorActionType.Fill, "{\"target\":\"field\",\"value\":\"x\",\"extra\":true}")]
    [InlineData(NavigatorActionType.Select, "{\"target\":\"field\",\"value\":\"x\",\"extra\":true}")]
    [InlineData(NavigatorActionType.Scroll, "{\"timeout\":20,\"extra\":true}")]
    [InlineData(NavigatorActionType.Capture, "{\"extra\":true}")]
    public void UnknownPropertiesAreDenied(NavigatorActionType action, string json)
    {
        Assert.False(PlaywrightToolAllowlist.TryResolve(action, Args(json), new Uri("https://safe.example"), true,
            out _, out _, out var denial));
        Assert.Equal("invalid_arguments", denial);
    }

    [Fact]
    public void AcceptedArgumentsAreTrimmedAndOnlyValidatedPropertiesAreReturned()
    {
        Assert.True(PlaywrightToolAllowlist.TryResolve(NavigatorActionType.Click, Args("{\"target\":\"  Save  \"}"),
            null, false, out _, out var normalized, out _));
        Assert.Equal("Save", normalized.GetProperty("target").GetString());
    }
}
