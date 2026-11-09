using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using OpenConnect.Abstractions.Persistence;

namespace OpenConnect.Hosting.AspNetCore.Protection;

/// <summary>Protects native agent session state using the host-configured ASP.NET Core Data Protection key ring.</summary>
/// <remarks>
/// Hosts are responsible for configuring a durable, shared key ring and its rotation policy. This adapter
/// neither configures nor persists keys, and binding data is not an authorization grant: callers must still
/// authenticate and authorize the current actor before restoring a session. Key-ring loss prevents restoration.
/// </remarks>
public sealed class DataProtectionNativeAgentSessionProtector : INativeAgentSessionProtector
{
    private const string Purpose = "OpenConnect.NativeAgentSession.v1";
    private readonly IDataProtectionProvider _provider;

    /// <summary>Creates a protector backed by the host-supplied Data Protection provider.</summary>
    /// <param name="provider">The configured key-ring provider.</param>
    /// <exception cref="ArgumentNullException">The provider is null.</exception>
    public DataProtectionNativeAgentSessionProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    public Task<byte[]> ProtectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> plaintext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ct.ThrowIfCancellationRequested();

        var input = plaintext.ToArray();
        byte[] protectedBytes;
        try
        {
            protectedBytes = CreateProtector(binding).Protect(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
        if (ct.IsCancellationRequested)
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult(protectedBytes);
    }

    /// <inheritdoc />
    public Task<byte[]> UnprotectAsync(NativeAgentSessionBinding binding, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ct.ThrowIfCancellationRequested();

        var input = ciphertext.ToArray();
        byte[] plaintext;
        try
        {
            plaintext = CreateProtector(binding).Unprotect(input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
        if (ct.IsCancellationRequested)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            ct.ThrowIfCancellationRequested();
        }

        return Task.FromResult(plaintext);
    }

    private IDataProtector CreateProtector(NativeAgentSessionBinding binding) =>
        _provider.CreateProtector(Purpose)
            .CreateProtector(nameof(binding.SessionId), binding.SessionId.Value.ToString())
            .CreateProtector(nameof(binding.TenantId), binding.TenantId.Value.ToString())
            .CreateProtector(nameof(binding.PrincipalId), binding.PrincipalId.Value.ToString())
            .CreateProtector(nameof(binding.AgentId), binding.AgentId)
            .CreateProtector(nameof(binding.DefinitionRevision), binding.DefinitionRevision)
            .CreateProtector(nameof(binding.MafVersion), binding.MafVersion)
            .CreateProtector(nameof(binding.SchemaVersion), binding.SchemaVersion)
            .CreateProtector(nameof(binding.ProviderId), binding.ProviderId);
}
