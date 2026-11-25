using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Rules;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class DetectorTokenTtlTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.ExternalModel);
    private static readonly PrivacyScope Scope = new(Context.Tenant, Context.Principal, Context.Session, "redaction");
    private const string Input = "Contact jane.doe@example.com today";

    [Fact]
    public async Task DefaultTtlIsThirtyMinutes()
    {
        var clock = new MutableTimeProvider();
        using var vault = new TokenVault(new byte[32], clock);
        var token = await SanitizeAsync(new RulesSecurityDetector(vault));

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal("jane.doe@example.com", await vault.ResolveAsync(token, Scope));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await vault.ResolveAsync(token, Scope));
        Assert.Equal(TimeSpan.FromMinutes(30), new PrivacyOptions().DefaultTokenTtl);
    }

    [Fact]
    public async Task ExplicitTtlIsApplied()
    {
        var clock = new MutableTimeProvider();
        using var vault = new TokenVault(new byte[32], clock);
        var token = await SanitizeAsync(new RulesSecurityDetector(vault, null, TimeSpan.FromMinutes(1)));

        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("jane.doe@example.com", await vault.ResolveAsync(token, Scope));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Null(await vault.ResolveAsync(token, Scope));
    }

    [Fact]
    public async Task RegisteredDetectorUsesConfiguredDefaultTokenTtl()
    {
        var clock = new MutableTimeProvider();
        var services = new ServiceCollection();
        services.AddOpenConnectRulesDetector();
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ISecurityDetector));
        using var vault = new TokenVault(new byte[32], clock);
        var options = Options.Create(new PrivacyOptions { DefaultTokenTtl = TimeSpan.FromMinutes(1) });
        var provider = new StubServiceProvider(vault, options);

        var detector = Assert.IsAssignableFrom<ISecurityDetector>(descriptor.ImplementationFactory!(provider));
        var token = await SanitizeAsync(detector);
        Assert.Equal("jane.doe@example.com", await vault.ResolveAsync(token, Scope));
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await vault.ResolveAsync(token, Scope));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveTtlIsRejected(int minutes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new RulesSecurityDetector(null, null, TimeSpan.FromMinutes(minutes)));

    private static async Task<string> SanitizeAsync(ISecurityDetector detector)
    {
        var result = await detector.DetectAndSanitizeAsync(Input, Context);
        var start = result.SanitizedContent.IndexOf("[OC_", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var end = result.SanitizedContent.IndexOf(']', start);
        return result.SanitizedContent[start..(end + 1)];
    }

    private sealed class StubServiceProvider(ITokenVault vault, IOptions<PrivacyOptions> options) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ITokenVault) ? vault :
            serviceType == typeof(IOptions<PrivacyOptions>) ? options : null;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }
}
