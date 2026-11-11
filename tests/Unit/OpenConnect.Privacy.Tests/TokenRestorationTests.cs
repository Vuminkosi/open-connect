using OpenConnect.Abstractions;
using OpenConnect.Privacy.Restoration;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class TokenRestorationTests
{
    private static readonly EgressContext Context = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), EgressDestination.Output);

    [Fact]
    public async Task RestoresTokensInFinalUserOutputWhenScopeMatches()
    {
        using var vault = new TokenVault(new byte[32]);
        var scope = new PrivacyScope(Context.Tenant, Context.Principal, Context.Session, "model");
        var token = await vault.StoreAsync("sensitive@example.com", "EMAIL", scope, TimeSpan.FromMinutes(10));

        var service = new TokenRestorationService(vault);
        var restored = await service.RestoreAsync($"Contact us at {token} anytime.", scope, new RestorationContext(RestorationTarget.FinalUserOutput));

        Assert.Equal("Contact us at sensitive@example.com anytime.", restored);
    }

    [Fact]
    public async Task RefusesRestorationAcrossMismatchedScope()
    {
        using var vault = new TokenVault(new byte[32]);
        var originalScope = new PrivacyScope(Context.Tenant, Context.Principal, Context.Session, "model");
        var foreignScope = new PrivacyScope(new TenantId(Ulid.NewUlid()), Context.Principal, Context.Session, "model");
        var token = await vault.StoreAsync("sensitive@example.com", "EMAIL", originalScope, TimeSpan.FromMinutes(10));

        var service = new TokenRestorationService(vault);
        var restored = await service.RestoreAsync($"Contact us at {token} anytime.", foreignScope, new RestorationContext(RestorationTarget.FinalUserOutput));

        // Foreign scope cannot resolve the token; placeholder remains intact
        Assert.Equal($"Contact us at {token} anytime.", restored);
    }

    [Fact]
    public async Task RestoresOnlyAllowlistedToolArgumentFields()
    {
        using var vault = new TokenVault(new byte[32]);
        var scope = new PrivacyScope(Context.Tenant, Context.Principal, Context.Session, "tool");
        var emailToken = await vault.StoreAsync("alice@example.com", "EMAIL", scope, TimeSpan.FromMinutes(10));
        var secretToken = await vault.StoreAsync("super_secret_key", "SECRET", scope, TimeSpan.FromMinutes(10));

        var jsonArgs = $"{{\"recipient\":\"{emailToken}\",\"apiKey\":\"{secretToken}\"}}";

        var service = new TokenRestorationService(vault);
        var context = new RestorationContext(RestorationTarget.ToolArguments, new HashSet<string> { "recipient" });

        var restored = await service.RestoreAsync(jsonArgs, scope, context);

        Assert.Contains("alice@example.com", restored);
        // apiKey is NOT in allowlisted fields, must NOT be restored!
        Assert.Contains(secretToken, restored);
        Assert.DoesNotContain("super_secret_key", restored);
    }

    [Fact]
    public async Task NeverRestoresTokensInDiagnosticsTarget()
    {
        using var vault = new TokenVault(new byte[32]);
        var scope = new PrivacyScope(Context.Tenant, Context.Principal, Context.Session, "diag");
        var token = await vault.StoreAsync("alice@example.com", "EMAIL", scope, TimeSpan.FromMinutes(10));

        var service = new TokenRestorationService(vault);
        var restored = await service.RestoreAsync($"Audit log: {token}", scope, new RestorationContext(RestorationTarget.Diagnostics));

        Assert.Equal($"Audit log: {token}", restored);
    }
}
