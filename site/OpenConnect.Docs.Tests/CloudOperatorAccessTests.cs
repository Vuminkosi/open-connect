using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudOperatorAccessTests
{
    [Fact]
    public void OperatorRequiresEnabledProviderAndExactIssuerSubjectPair()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Issuer"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        }).Build();
        var access = new CloudOperatorAccess(configuration);
        ClaimsPrincipal Principal(string subject, string issuer, bool authenticated = true) => new(
            new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)], authenticated ? "test" : null));
        Assert.True(access.IsAllowed(Principal("operator", "https://issuer.example")));
        Assert.False(access.IsAllowed(Principal("other", "https://issuer.example")));
        Assert.False(access.IsAllowed(Principal("operator", "https://other.example")));
        Assert.False(access.IsAllowed(Principal("operator", "https://issuer.example", false)));
        configuration["OpenConnect:Cloud:Identity:Enabled"] = "false";
        Assert.False(access.IsAllowed(Principal("operator", "https://issuer.example")));
        configuration["OpenConnect:Cloud:Identity:Google:Enabled"] = "true";
        var google = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "operator"),
            new Claim(CloudIdentity.IssuerClaim, CloudIdentity.GoogleIssuer, ClaimValueTypes.String, "OpenConnect.Cloud")], "test"));
        Assert.False(access.IsAllowed(google));
        configuration["OpenConnect:Cloud:Operators:0:Issuer"] = CloudIdentity.GoogleIssuer;
        Assert.True(access.IsAllowed(google));
        configuration["OpenConnect:Cloud:Identity:Google:Enabled"] = "false";
        Assert.False(access.IsAllowed(google));
    }
}
