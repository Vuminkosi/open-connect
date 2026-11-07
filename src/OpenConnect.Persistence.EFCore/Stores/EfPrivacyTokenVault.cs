using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfPrivacyTokenVault<TContext>(TContext context) : IPrivacyTokenVault where TContext : DbContext
{
    public async Task StoreAsync(PrivacyTokenBinding binding, CancellationToken ct = default)
    {
        var tenantScope = UlidGuidMapper.Scope(binding.TenantId.Value);
        var scope = binding.PrincipalOrWorkloadScope;
        var sessionId = UlidGuidMapper.ToGuid(binding.SessionId.Value);
        // EF-02F master-approved interim mapping. Envelope is opaque; this adapter never decrypts it.
        context.Set<PrivacyTokenEntity>().Add(new PrivacyTokenEntity
        {
            Id = Guid.NewGuid(), TenantScope = tenantScope, PrincipalOrWorkloadScope = scope,
            SessionId = sessionId, TurnId = null, TokenId = binding.TokenId, EntityType = binding.EntityType,
            EncryptedValue = binding.EncryptedValue, Nonce = [], AuthenticationTag = [], WrappedDataKey = [],
            KeyProviderId = "envelope", KeyVersion = "v1",
            BindingDigest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{tenantScope}|{scope}|{sessionId}|{binding.TokenId}|{binding.EntityType}"))).ToLowerInvariant(),
            CreatedUtc = DateTime.UtcNow, ExpiresUtc = binding.ExpiresAt.UtcDateTime
        });
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PrivacyTokenBinding?> GetAsync(string tokenId, TenantId tenantId, CancellationToken ct = default)
    {
        var tenantScope = UlidGuidMapper.Scope(tenantId.Value);
        var row = await context.Set<PrivacyTokenEntity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TokenId == tokenId && x.TenantScope == tenantScope, ct).ConfigureAwait(false);
        return row is null ? null : new PrivacyTokenBinding(row.TokenId, tenantId, row.PrincipalOrWorkloadScope,
            new SessionId(UlidGuidMapper.FromGuid(row.SessionId)), row.EntityType, row.EncryptedValue,
            new DateTimeOffset(DateTime.SpecifyKind(row.ExpiresUtc, DateTimeKind.Utc)));
    }

    public async Task DeleteExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var rows = await context.Set<PrivacyTokenEntity>().Where(x => x.ExpiresUtc <= nowUtc.UtcDateTime).Take(500).ToListAsync(ct).ConfigureAwait(false);
        context.RemoveRange(rows);
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
