using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Hosting.AspNetCore.Protection;
using Xunit;

namespace OpenConnect.Hosting.Tests;

public sealed class NativeAgentSessionProtectionTests
{
    private static readonly byte[] State = [1, 2, 3, 4, 5];

    [Fact]
    public async Task Protect_and_unprotect_round_trip_for_exact_binding()
    {
        var protector = CreateProtector();
        var binding = CreateBinding();
        var ciphertext = await protector.ProtectAsync(binding, State);
        var restored = await protector.UnprotectAsync(binding, ciphertext);

        Assert.NotEqual(State, ciphertext);
        Assert.Equal(State, restored);
        CryptographicOperations.ZeroMemory(restored);
        CryptographicOperations.ZeroMemory(ciphertext);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task Every_binding_field_is_authenticated(int field)
    {
        var protector = CreateProtector();
        var binding = CreateBinding();
        var changedBinding = ChangeBinding(field);
        AssertBindingFieldChanged(binding, changedBinding, field);

        var ciphertext = await protector.ProtectAsync(binding, State);
        await Assert.ThrowsAsync<CryptographicException>(() => protector.UnprotectAsync(changedBinding, ciphertext));
        CryptographicOperations.ZeroMemory(ciphertext);
    }

    [Fact]
    public async Task Tampering_is_rejected()
    {
        var protector = CreateProtector();
        var ciphertext = await protector.ProtectAsync(CreateBinding(), State);
        ciphertext[^1] ^= 0x40;

        await Assert.ThrowsAsync<CryptographicException>(() => protector.UnprotectAsync(CreateBinding(), ciphertext));
        CryptographicOperations.ZeroMemory(ciphertext);
    }

    [Fact]
    public void Resolving_without_host_provider_fails_closed()
    {
        var services = new ServiceCollection().AddOpenConnectNativeAgentSessionProtection();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<INativeAgentSessionProtector>());
    }

    [Fact]
    public async Task Cancellation_before_operation_is_honored()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProtector().ProtectAsync(CreateBinding(), State, cancellation.Token));
    }

    [Fact]
    public void Registration_is_idempotent_and_preserves_existing_registration()
    {
        var existing = new TestProtector();
        var services = new ServiceCollection();
        services.AddSingleton<INativeAgentSessionProtector>(existing);
        var returned = services.AddOpenConnectNativeAgentSessionProtection()
            .AddOpenConnectNativeAgentSessionProtection();
        using var provider = returned.BuildServiceProvider();

        Assert.Same(services, returned);
        Assert.Same(existing, provider.GetRequiredService<INativeAgentSessionProtector>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(INativeAgentSessionProtector));
    }

    private static INativeAgentSessionProtector CreateProtector() =>
        new DataProtectionNativeAgentSessionProtector(new EphemeralDataProtectionProvider());

    private static NativeAgentSessionBinding CreateBinding(int changedField = -1) => new(
        new SessionId(Ulid.Parse(changedField == 0 ? "01ARZ3NDEKTSV4RRFFQ69G5FAW" : "01ARZ3NDEKTSV4RRFFQ69G5FAV")),
        new TenantId(Ulid.Parse(changedField == 1 ? "01ARZ3NDEKTSV4RRFFQ69G5FAW" : "01ARZ3NDEKTSV4RRFFQ69G5FAV")),
        new PrincipalId(Ulid.Parse(changedField == 2 ? "01ARZ3NDEKTSV4RRFFQ69G5FAW" : "01ARZ3NDEKTSV4RRFFQ69G5FAV")),
        changedField == 3 ? "Agent" : "agent", changedField == 4 ? "r2" : "r1", changedField == 5 ? "2.1" : "2.0",
        changedField == 6 ? "schema-2" : "schema-1", changedField == 7 ? "provider-b" : "provider-a");

    private static NativeAgentSessionBinding ChangeBinding(int field) => CreateBinding(field);

    private static void AssertBindingFieldChanged(
        NativeAgentSessionBinding original,
        NativeAgentSessionBinding changed,
        int field)
    {
        Assert.NotEqual(original, changed);
        switch (field)
        {
            case 0: Assert.NotEqual(original.SessionId, changed.SessionId); break;
            case 1: Assert.NotEqual(original.TenantId, changed.TenantId); break;
            case 2: Assert.NotEqual(original.PrincipalId, changed.PrincipalId); break;
            case 3: Assert.NotEqual(original.AgentId, changed.AgentId); break;
            case 4: Assert.NotEqual(original.DefinitionRevision, changed.DefinitionRevision); break;
            case 5: Assert.NotEqual(original.MafVersion, changed.MafVersion); break;
            case 6: Assert.NotEqual(original.SchemaVersion, changed.SchemaVersion); break;
            case 7: Assert.NotEqual(original.ProviderId, changed.ProviderId); break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }
    }

    private sealed class TestProtector : INativeAgentSessionProtector
    {
        public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
        public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default) => Task.FromResult(Array.Empty<byte>());
    }
}
