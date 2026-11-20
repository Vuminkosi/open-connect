using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudIdentityTests
{
    [Fact]
    public void ProviderSuppliedIssuerMarkerCannotOverrideLegacyIssuer()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "same-subject"),
            new Claim(CloudIdentity.IssuerClaim, CloudIdentity.GoogleIssuer, ClaimValueTypes.String, "https://issuer.example")
        ], "test"));

        Assert.Equal(new CloudSubject("https://issuer.example", "same-subject"),
            CloudSubjectResolver.FromPrincipal(principal, "https://issuer.example"));
        Assert.False(CloudIdentity.IsGoogle(principal));
    }

    [Fact]
    public void LocallyStampedGoogleIssuerSeparatesMatchingSubjects()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "same-subject"),
            new Claim(CloudIdentity.IssuerClaim, CloudIdentity.GoogleIssuer, ClaimValueTypes.String, "OpenConnect.Cloud")
        ], "test"));

        Assert.Equal(new CloudSubject(CloudIdentity.GoogleIssuer, "same-subject"),
            CloudSubjectResolver.FromPrincipal(principal, "https://issuer.example"));
        Assert.True(CloudIdentity.IsGoogle(principal));
    }
    [Fact]
    public async Task GoogleConfigurationUsesCodePkceAndStampsOnlyTheValidatedIdentity()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var sectionConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Google:Enabled"] = "true",
            ["Identity:Google:ClientId"] = "client",
            ["Identity:Google:ClientSecret"] = "secret"
        }).Build();
        services.AddCloudIdentityProviders(sectionConfig.GetSection("Identity"));
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(CloudIdentity.GoogleScheme);
        Assert.Equal(CloudIdentity.GoogleIssuer, options.Authority);
        Assert.Equal("code", options.ResponseType);
        Assert.Equal("query", options.ResponseMode);
        Assert.True(options.UsePkce);
        Assert.False(options.SaveTokens);
        Assert.Equal("/cloud/signin-google", options.CallbackPath.Value);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user"),
            new Claim(CloudIdentity.IssuerClaim, "https://attacker.example")
        ], "oidc"));
        var context = new TokenValidatedContext(new DefaultHttpContext(),
            new AuthenticationScheme(CloudIdentity.GoogleScheme, null, typeof(OpenIdConnectHandler)),
            options, principal, new AuthenticationProperties());

        await options.Events.OnTokenValidated(context);

        Assert.Equal(new CloudSubject(CloudIdentity.GoogleIssuer, "user"), CloudSubjectResolver.FromPrincipal(principal, "https://other.example"));
        Assert.Single(principal.FindAll(CloudIdentity.IssuerClaim));
    }

    [Fact]
    public void EnablingGoogleWithoutCredentialsFailsStartup()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Google:Enabled"] = "true"
        }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCloudIdentityProviders(config.GetSection("Identity")));
    }
    [Theory]
    [InlineData("/cloud/organizations", "/cloud/organizations")]
    [InlineData("//evil.example", "/cloud")]
    [InlineData("/cloud/\\evil.example", "/cloud")]
    [InlineData("https://evil.example/cloud", "/cloud")]
    [InlineData("/cloudy", "/cloud")]
    public void ReturnDestinationCannotLeaveCloud(string supplied, string expected) =>
        Assert.Equal(expected, CloudIdentityEndpoints.CloudReturnUrl(supplied));
    [Fact]
    public void DisablingProviderRejectsExistingSessions()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Enabled"] = "true",
            ["Identity:Authority"] = "https://issuer.example",
            ["Identity:Google:Enabled"] = "false"
        }).Build().GetSection("Identity");
        var google = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user"),
            new Claim(CloudIdentity.IssuerClaim, CloudIdentity.GoogleIssuer, ClaimValueTypes.String, "OpenConnect.Cloud")
        ], "test"));
        var legacy = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "user", ClaimValueTypes.String, "https://issuer.example")
        ], "test"));

        Assert.False(CloudIdentity.IsEnabled(google, configuration));
        Assert.True(CloudIdentity.IsEnabled(legacy, configuration));
        configuration["Authority"] = "https://other.example";
        Assert.False(CloudIdentity.IsEnabled(legacy, configuration));
        configuration["Enabled"] = "false";
        Assert.False(CloudIdentity.IsEnabled(legacy, configuration));
    }
}
