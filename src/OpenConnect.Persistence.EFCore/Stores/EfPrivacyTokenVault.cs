using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Persistence;
using OpenConnect.Persistence.EFCore.Entities;

namespace OpenConnect.Persistence.EFCore.Stores;

internal sealed class EfPrivacyTokenVault<TContext>(TContext context, TimeProvider? timeProvider = null) : IPrivacyTokenVault where TContext : DbContext
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task StoreAsync(PrivacyTokenBinding binding, CancellationToken ct = default)
    {
        var tenantScope = UlidGuidMapper.Scope(binding.TenantId.Value);
        var scope = binding.PrincipalOrWorkloadScope;
        var sessionId = UlidGuidMapper.ToGuid(binding.SessionId.Value);
        var digest = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{tenantScope}|{scope}|{sessionId}|{binding.TokenId}|{binding.EntityType}"))).ToLowerInvariant();
        var createdUtc = _time.GetUtcNow().UtcDateTime;
        // Storing the same (tenant, principal/workload, token, session) again replaces the existing binding so the
        // unique index never rejects an idempotent re-store. Concurrent first writes still surface DbUpdateException.
        var existing = await context.Set<PrivacyTokenEntity>()
            .FirstOrDefaultAsync(x => x.TenantScope == tenantScope && x.PrincipalOrWorkloadScope == scope
                && x.SessionId == sessionId && x.TokenId == binding.TokenId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            existing.EntityType = binding.EntityType;
            existing.EncryptedValue = binding.EncryptedValue;
            existing.BindingDigest = digest;
            existing.CreatedUtc = createdUtc;
            existing.ExpiresUtc = binding.ExpiresAt.UtcDateTime;
        }
        else
        {
            // EF-02F master-approved interim mapping. Envelope is opaque; this adapter never decrypts it.
            context.Set<PrivacyTokenEntity>().Add(new PrivacyTokenEntity
            {
                Id = Guid.NewGuid(),
                TenantScope = tenantScope,
                PrincipalOrWorkloadScope = scope,
                SessionId = sessionId,
                TurnId = null,
                TokenId = binding.TokenId,
                EntityType = binding.EntityType,
                EncryptedValue = binding.EncryptedValue,
                Nonce = [],
                AuthenticationTag = [],
                WrappedDataKey = [],
                KeyProviderId = "envelope",
                KeyVersion = "v1",
                BindingDigest = digest,
                CreatedUtc = createdUtc,
                ExpiresUtc = binding.ExpiresAt.UtcDateTime
            });
        }
        await context.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<PrivacyTokenBinding?> GetAsync(string tokenId, TenantId tenantId, string principalOrWorkloadScope, SessionId sessionId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tokenId) || string.IsNullOrWhiteSpace(principalOrWorkloadScope))
            return null;
        var tenantScope = UlidGuidMapper.Scope(tenantId.Value);
        var session = UlidGuidMapper.ToGuid(sessionId.Value);
        var nowUtc = _time.GetUtcNow().UtcDateTime;
        // Fail closed: a binding is visible only to its exact tenant, principal/workload and session, and only before expiry.
        var row = await context.Set<PrivacyTokenEntity>().AsNoTracking()
            .Where(x => x.TokenId == tokenId && x.TenantScope == tenantScope
                && x.PrincipalOrWorkloadScope == principalOrWorkloadScope && x.SessionId == session
                && x.ExpiresUtc > nowUtc)
            .OrderByDescending(x => x.CreatedUtc).ThenBy(x => x.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
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
