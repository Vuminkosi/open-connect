using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace OpenConnect.Docs.Cloud;

public sealed class EfCloudCredentialStore(IDbContextFactory<CloudControlPlaneDbContext> factory) : ICloudCredentialStore
{
    public async Task<ICloudCredentialSession> OpenAsync(bool transactional, CancellationToken cancellationToken)
    {
        var db = await factory.CreateDbContextAsync(cancellationToken);
        try
        {
            var transaction = transactional ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken) : null;
            return new Session(db, transaction);
        }
        catch { await db.DisposeAsync(); throw; }
    }

    private sealed class Session(CloudControlPlaneDbContext db, IDbContextTransaction? transaction) : ICloudCredentialSession
    {
        public Task<string?> FindRoleAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
            db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId && x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId)
                .Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);

        public Task<CloudCredentialApplication?> FindApplicationAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
            db.Applications.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == applicationId)
                .Select(x => new CloudCredentialApplication(x.Id, x.Environment)).SingleOrDefaultAsync(cancellationToken);

        public Task<CloudCredential?> FindAsync(Guid credentialId, Guid? tenantId, Guid? applicationId, CancellationToken cancellationToken) =>
            db.Credentials.SingleOrDefaultAsync(x => x.Id == credentialId && (tenantId == null || x.TenantId == tenantId) &&
                (applicationId == null || x.ApplicationId == applicationId), cancellationToken);

        public async Task<IReadOnlyList<CloudCredential>> ListAsync(Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
            await db.Credentials.AsNoTracking().Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId)
                .OrderByDescending(x => x.IssuedAt).ToListAsync(cancellationToken);

        public Task<CloudCapabilitySnapshot?> GetCapabilitiesAsync(CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken) =>
            EfCloudCapabilityReader.LoadAsync(db, subject, tenantId, applicationId, cancellationToken);

        public async Task<bool> TouchIfActiveAsync(Guid credentialId, DateTimeOffset now, CancellationToken cancellationToken) =>
            await db.Credentials.Where(x => x.Id == credentialId && x.RevokedAt == null && x.ExpiresAt != null && x.ExpiresAt > now &&
                db.Applications.Any(app => app.TenantId == x.TenantId && app.Id == x.ApplicationId && app.Environment == x.Environment))
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastUsedAt, now), cancellationToken) == 1;

        public void Add(CloudCredential credential) => db.Credentials.Add(credential);
        public void AddAudit(CloudSubject subject, Guid tenantId, string role, string action, Guid credentialId, DateTimeOffset now) =>
            db.AuditEvents.Add(new CloudAuditEvent { Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = subject.Issuer,
                SubjectId = subject.SubjectId, ActorRole = role, Action = action, ResourceId = credentialId.ToString("D"), Outcome = "succeeded", OccurredAt = now });

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            if (transaction is null) throw new InvalidOperationException("A transaction is required for credential writes.");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            try { if (transaction is not null) await transaction.DisposeAsync(); }
            finally { await db.DisposeAsync(); }
        }
    }
}
