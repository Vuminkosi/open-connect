using System.Text;
using OpenConnect.Abstractions;
using OpenConnect.Privacy.Restoration;
using OpenConnect.Privacy.Streaming;
using OpenConnect.Privacy.Tokenization;
using Xunit;

namespace OpenConnect.Privacy.Tests;

public sealed class SecretRestorationTests
{
    private static readonly PrivacyScope Scope = new(new(Ulid.NewUlid()), new(Ulid.NewUlid()), new(Ulid.NewUlid()), "restore");

    public static TheoryData<string> SecretCategories => new() { "SECRET", "CREDENTIAL", "API_KEY", "PRIVATE_KEY", "ACCESS_TOKEN", "PASSWORD" };

    private class DelegatingVault(ITokenVault inner) : ITokenVault
    {
        public ValueTask<string> StoreAsync(string value, string entityType, PrivacyScope scope, TimeSpan ttl, CancellationToken cancellationToken = default) =>
            inner.StoreAsync(value, entityType, scope, ttl, cancellationToken);

        public ValueTask<string?> ResolveAsync(string token, PrivacyScope scope, CancellationToken cancellationToken = default) =>
            inner.ResolveAsync(token, scope, cancellationToken);
    }

    private sealed class SecretAwareWrapper(TokenVault inner) : DelegatingVault(inner), ISecretAwareTokenVault
    {
        public bool IsSecretToken(string token, PrivacyScope scope) => inner.IsSecretToken(token, scope);
    }

    private sealed class ThrowingVault(TokenVault inner) : DelegatingVault(inner), ISecretAwareTokenVault
    {
        public bool IsSecretToken(string token, PrivacyScope scope) => throw new InvalidOperationException("category lookup failed");
    }

    private static async Task<string> StreamAsync(ITokenVault vault, PrivacyScope scope, string text, int chunkSize)
    {
        var buffer = new StreamingPlaceholderBuffer(vault, scope);
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            sb.Append(await buffer.ProcessChunkAsync(text.Substring(i, Math.Min(chunkSize, text.Length - i))));
        }
        sb.Append(await buffer.FlushAsync());
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(SecretCategories))]
    public async Task StreamingNeverRestoresSecretCategoryForAnyChunkSize(string category)
    {
        using var vault = new TokenVault(new byte[32]);
        var secret = await vault.StoreAsync("synthetic-secret-value", category, Scope, TimeSpan.FromMinutes(5));
        var email = await vault.StoreAsync("a@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(5));
        var text = $"k={secret} e={email}.";

        for (var size = 1; size <= text.Length; size++)
        {
            Assert.Equal($"k={secret} e=a@example.com.", await StreamAsync(vault, Scope, text, size));
        }
    }

    [Theory]
    [MemberData(nameof(SecretCategories))]
    public async Task WrapperVaultWithoutCapabilityDeniesAllRestorationInBothTargets(string category)
    {
        using var inner = new TokenVault(new byte[32]);
        var wrapper = new DelegatingVault(inner);
        var secret = await inner.StoreAsync("synthetic-secret-value", category, Scope, TimeSpan.FromMinutes(5));
        var email = await inner.StoreAsync("a@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(5));
        var service = new TokenRestorationService(wrapper);

        var text = $"{secret} {email}";
        Assert.Equal(text, await service.RestoreAsync(text, Scope, new RestorationContext(RestorationTarget.FinalUserOutput)));

        var json = $"{{\"a\":\"{secret}\",\"b\":\"{email}\"}}";
        var restored = await service.RestoreAsync(json, Scope, new RestorationContext(RestorationTarget.ToolArguments, new HashSet<string> { "a", "b" }));
        Assert.DoesNotContain("synthetic-secret-value", restored);
        Assert.DoesNotContain("a@example.com", restored);
        Assert.Equal(text, await StreamAsync(wrapper, Scope, text, 3));
    }

    [Theory]
    [MemberData(nameof(SecretCategories))]
    public async Task SecretAwareWrapperRestoresOnlyNonSecretTokens(string category)
    {
        using var inner = new TokenVault(new byte[32]);
        var wrapper = new SecretAwareWrapper(inner);
        var secret = await inner.StoreAsync("synthetic-secret-value", category, Scope, TimeSpan.FromMinutes(5));
        var email = await inner.StoreAsync("a@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(5));
        var service = new TokenRestorationService(wrapper);

        Assert.Equal($"{secret} a@example.com", await service.RestoreAsync($"{secret} {email}", Scope, new RestorationContext(RestorationTarget.FinalUserOutput)));

        var json = $"{{\"a\":\"{secret}\",\"b\":\"{email}\"}}";
        var restored = await service.RestoreAsync(json, Scope, new RestorationContext(RestorationTarget.ToolArguments, new HashSet<string> { "a", "b" }));
        Assert.DoesNotContain("synthetic-secret-value", restored);
        Assert.Contains("a@example.com", restored);
    }

    [Fact]
    public async Task StreamingAndNonStreamingProduceIdenticalOutput()
    {
        using var vault = new TokenVault(new byte[32]);
        var text = $"x {await vault.StoreAsync("s1", "API_KEY", Scope, TimeSpan.FromMinutes(5))} y {await vault.StoreAsync("e@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(5))} [OC_UNKNOWN] z";
        var service = new TokenRestorationService(vault);
        var expected = await service.RestoreAsync(text, Scope, new RestorationContext(RestorationTarget.FinalUserOutput));

        foreach (var size in new[] { 1, 2, 5, 13, text.Length })
        {
            Assert.Equal(expected, await StreamAsync(vault, Scope, text, size));
        }
        Assert.DoesNotContain("s1", expected);
        Assert.Contains("e@example.com", expected);
    }

    [Fact]
    public async Task VaultWhoseCategoryCheckThrowsIsDenied()
    {
        using var inner = new TokenVault(new byte[32]);
        var vault = new ThrowingVault(inner);
        var email = await inner.StoreAsync("a@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(5));

        Assert.Equal(email, await new TokenRestorationService(vault).RestoreAsync(email, Scope, new RestorationContext(RestorationTarget.FinalUserOutput)));
        Assert.Equal(email, await StreamAsync(vault, Scope, email, 2));
    }

    [Fact]
    public async Task CrossScopeTokenIsNotRestoredByEitherPath()
    {
        using var vault = new TokenVault(new byte[32]);
        var other = new PrivacyScope(Scope.Tenant, Scope.Principal, Scope.Session, "other");
        var email = await vault.StoreAsync("a@example.com", "EMAIL", Scope, TimeSpan.FromMinutes(5));

        Assert.Equal(email, await new TokenRestorationService(vault).RestoreAsync(email, other, new RestorationContext(RestorationTarget.FinalUserOutput)));
        Assert.Equal(email, await StreamAsync(vault, other, email, 3));
    }
}
