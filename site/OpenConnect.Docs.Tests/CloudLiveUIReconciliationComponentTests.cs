using System.Security.Claims;
using Bunit;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using OpenConnect.Docs.Components.Pages.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudLiveUIReconciliationComponentTests : BunitContext
{
    [Theory]
    [InlineData("operator", true)]
    [InlineData("other", false)]
    public void ShowsUnknownUsageOnlyToConfiguredOperator(string subject, bool allowed)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:Cloud:Identity:Enabled"] = "true",
            ["OpenConnect:Cloud:Identity:Authority"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Issuer"] = "https://issuer.example",
            ["OpenConnect:Cloud:Operators:0:Subject"] = "operator"
        }).Build();
        var reader = new Reader();
        Services.AddSingleton<IConfiguration>(config);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton<CloudOperatorAccess>();
        Services.AddSingleton<ICloudLiveUIReconciliationReader>(reader);
        Services.AddSingleton<CloudLiveUIReconciliation>();
        Services.AddSingleton<AuthenticationStateProvider>(new TestAuthentication(subject));
        var component = Render<OperatorLiveUIReconciliation>();
        if (allowed)
        {
            Assert.Contains("Actual tokens", component.Markup);
            Assert.Contains("Unknown", component.Markup);
            Assert.Contains(reader.RequestId.ToString(), component.Markup);
            Assert.Equal(1, reader.Calls);
        }
        else
        {
            Assert.Contains("Operator access is required", component.Markup);
            Assert.DoesNotContain(reader.RequestId.ToString(), component.Markup);
            Assert.Equal(0, reader.Calls);
        }
    }

    private sealed class TestAuthentication(string subject) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, "https://issuer.example")], "test"))));
    }

    private sealed class Reader : ICloudLiveUIReconciliationReader
    {
        public Guid RequestId { get; } = Guid.NewGuid();
        public int Calls { get; private set; }
        public Task<IReadOnlyList<CloudLiveUIUnknownUsage>> ReadUnknownAsync(CloudLiveUIReconciliationCursor? after, int count,
            DateTimeOffset cutoff, CancellationToken cancellationToken)
        {
            Calls++;
            IReadOnlyList<CloudLiveUIUnknownUsage> rows = [new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), RequestId,
                Guid.NewGuid(), cutoff, 2048)];
            return Task.FromResult(rows);
        }
    }
}
