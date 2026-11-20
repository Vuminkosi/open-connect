using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace OpenConnect.Docs.Cloud;

public sealed record CreateCloudCredentialRequest(IReadOnlyList<string>? Scopes, DateTimeOffset? ExpiresAt);
public sealed record RotateCloudCredentialRequest(DateTimeOffset? ExpiresAt);
public sealed record CloudCredentialSummary(
    Guid Id, Guid TenantId, Guid ApplicationId, string Environment, string Fingerprint,
    IReadOnlyList<string> Scopes, DateTimeOffset IssuedAt, DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt, DateTimeOffset? LastUsedAt);
public sealed record CloudCredentialIssueResult(string Status, CloudCredentialSummary? Credential = null, string? Secret = null);
public sealed record CloudCredentialIdentity(
    Guid TenantId, Guid ApplicationId, string Environment, IReadOnlySet<string> Scopes);

public sealed class CloudCredentialService(
    IDbContextFactory<CloudControlPlaneDbContext> contextFactory,
    TimeProvider timeProvider)
{
    private static readonly string[] Administrators = ["Owner", "Administrator"];

    public async Task<(IReadOnlyList<CloudCredentialSummary>? Credentials, bool Forbidden)> ListAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var role = await FindRoleAsync(db, subject, tenantId, cancellationToken);
        if (role is null) return (null, true);
        if (!await db.Applications.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == applicationId, cancellationToken))
        {
            return (null, true);
        }

        var rows = await db.Credentials.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.ApplicationId == applicationId)
            .OrderByDescending(x => x.IssuedAt)
            .ToListAsync(cancellationToken);
        var credentials = rows.Select(x => new CloudCredentialSummary(
            x.Id, x.TenantId, x.ApplicationId, x.Environment, x.Fingerprint,
            JsonSerializer.Deserialize<string[]>(x.Scopes) ?? [], x.IssuedAt, x.ExpiresAt, x.RevokedAt, x.LastUsedAt)).ToArray();
        return (credentials, false);
    }

    public async Task<CloudCredentialIssueResult> CreateAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, IReadOnlyList<string>? requestedScopes,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var role = await FindRoleAsync(db, subject, tenantId, cancellationToken);
        if (role is null) return new("not_found");
        if (!Administrators.Contains(role, StringComparer.Ordinal)) return new("forbidden");
        var scopesResult = await ValidateScopesAsync(db, subject, tenantId, applicationId, requestedScopes, cancellationToken);
        if (scopesResult.Status != "succeeded") return new(scopesResult.Status);
        var application = await db.Applications.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == applicationId, cancellationToken);
        if (application is null) return new("not_found");
        if (expiresAt is null || expiresAt <= timeProvider.GetUtcNow()) return new("invalid_expiration");
        var (credential, secret) = NewCredential(tenantId, application, scopesResult.Scopes!, expiresAt, timeProvider.GetUtcNow());
        db.Credentials.Add(credential);
        db.AuditEvents.Add(CreateAudit(subject, tenantId, role, "credential.created", credential.Id, timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("succeeded", ToSummary(credential), secret);
    }

    public async Task<CloudCredentialIssueResult> RotateAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, Guid credentialId,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var role = await FindRoleAsync(db, subject, tenantId, cancellationToken);
        if (role is null) return new("not_found");
        if (!Administrators.Contains(role, StringComparer.Ordinal)) return new("forbidden");
        var oldCredential = await db.Credentials.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ApplicationId == applicationId && x.Id == credentialId, cancellationToken);
        if (oldCredential is null) return new("not_found");
        if (oldCredential.RevokedAt is not null) return new("credential_revoked");
        if (expiresAt is null || expiresAt <= timeProvider.GetUtcNow()) return new("invalid_expiration");

        var scopes = JsonSerializer.Deserialize<string[]>(oldCredential.Scopes) ?? [];
        var scopesResult = await ValidateScopesAsync(db, subject, tenantId, applicationId, scopes, cancellationToken);
        if (scopesResult.Status != "succeeded") return new(scopesResult.Status);
        var application = await db.Applications.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == applicationId, cancellationToken);
        if (application is null) return new("not_found");

        var now = timeProvider.GetUtcNow();
        var (newCredential, secret) = NewCredential(tenantId, application, scopes, expiresAt, now);
        oldCredential.RevokedAt = now;
        db.Credentials.Add(newCredential);
        db.AuditEvents.Add(CreateAudit(subject, tenantId, role, "credential.rotated", oldCredential.Id, now));
        db.AuditEvents.Add(CreateAudit(subject, tenantId, role, "credential.created_by_rotation", newCredential.Id, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new("succeeded", ToSummary(newCredential), secret);
    }

    public async Task<string> RevokeAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, Guid credentialId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var role = await FindRoleAsync(db, subject, tenantId, cancellationToken);
        if (role is null) return "not_found";
        if (!Administrators.Contains(role, StringComparer.Ordinal)) return "forbidden";
        var credential = await db.Credentials.SingleOrDefaultAsync(x =>
            x.TenantId == tenantId && x.ApplicationId == applicationId && x.Id == credentialId, cancellationToken);
        if (credential is null) return "not_found";
        if (credential.RevokedAt is not null) return "succeeded";

        var now = timeProvider.GetUtcNow();
        credential.RevokedAt = now;
        db.AuditEvents.Add(CreateAudit(subject, tenantId, role, "credential.revoked", credentialId, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return "succeeded";
    }

    public async Task<CloudCredentialIdentity?> ValidateAsync(string? presentedSecret, CancellationToken cancellationToken)
    {
        if (!TryReadCredentialId(presentedSecret, out var credentialId)) return null;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var credential = await db.Credentials.SingleOrDefaultAsync(x => x.Id == credentialId, cancellationToken);
        if (credential is null || credential.RevokedAt is not null ||
            credential.ExpiresAt is null || credential.ExpiresAt <= timeProvider.GetUtcNow() ||
            !HasExpectedEnvironmentTag(presentedSecret!, credential.Environment))
        {
            return null;
        }

        var presentedBytes = Encoding.UTF8.GetBytes(presentedSecret!);
        var presentedHash = SHA256.HashData(presentedBytes);
        CryptographicOperations.ZeroMemory(presentedBytes);
        var matches = CryptographicOperations.FixedTimeEquals(presentedHash, credential.KeyHash);
        CryptographicOperations.ZeroMemory(presentedHash);
        if (!matches) return null;
        if (!await db.Applications.AsNoTracking().AnyAsync(
                x => x.TenantId == credential.TenantId && x.Id == credential.ApplicationId && x.Environment == credential.Environment,
                cancellationToken))
        {
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var updated = await db.Credentials.Where(x => x.Id == credential.Id && x.RevokedAt == null &&
                x.ExpiresAt != null && x.ExpiresAt > now &&
                db.Applications.Any(app => app.TenantId == x.TenantId && app.Id == x.ApplicationId && app.Environment == x.Environment))
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.LastUsedAt, now), cancellationToken);
        if (updated != 1) return null;
        var scopes = JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? [];
        return new CloudCredentialIdentity(credential.TenantId, credential.ApplicationId, credential.Environment,
            new HashSet<string>(scopes, StringComparer.Ordinal));
    }

    private async Task<(string Status, string[]? Scopes)> ValidateScopesAsync(
        CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, Guid applicationId, IReadOnlyList<string>? requestedScopes, CancellationToken cancellationToken)
    {
        if (requestedScopes is null || requestedScopes.Count is < 1 or > 32 ||
            requestedScopes.Any(string.IsNullOrWhiteSpace) || requestedScopes.Distinct(StringComparer.Ordinal).Count() != requestedScopes.Count)
        {
            return ("invalid_scopes", null);
        }

        var snapshot = await EfCloudCapabilityReader.LoadAsync(db, subject, tenantId, applicationId, cancellationToken);
        if (snapshot is null) return ("not_found", null);
        var capabilityViews = CloudCapabilityProjection.Application(snapshot, timeProvider.GetUtcNow());
        var byId = capabilityViews.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var scope in requestedScopes)
        {
            if (!byId.TryGetValue(scope, out var capability)) return ("invalid_scopes", null);
            if (!string.IsNullOrEmpty(capability.ReasonCode)) return (capability.ReasonCode, null);
        }
        return ("succeeded", requestedScopes.Order(StringComparer.Ordinal).ToArray());
    }

    private static (CloudCredential Credential, string Secret) NewCredential(
        Guid tenantId, CloudApplication application, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var tag = EnvironmentTag(application.Environment);
        var randomSecret = RandomNumberGenerator.GetBytes(32);
        var token = $"oc_{tag}_{id:N}_{Base64Url(randomSecret)}";
        CryptographicOperations.ZeroMemory(randomSecret);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var credential = new CloudCredential
        {
            Id = id, TenantId = tenantId, ApplicationId = application.Id, Environment = application.Environment,
            KeyHash = hash, Fingerprint = Convert.ToHexString(hash.AsSpan(0, 6)),
            Scopes = JsonSerializer.Serialize(scopes), IssuedAt = now, ExpiresAt = expiresAt?.ToUniversalTime(),
        };
        return (credential, token);
    }

    private static string EnvironmentTag(string environment) => environment switch
    {
        "Development" => "dev",
        "Staging" => "stg",
        "Production" => "prd",
        _ => throw new InvalidOperationException("The application environment is invalid."),
    };

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryReadCredentialId(string? token, out Guid id)
    {
        id = Guid.Empty;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 160) return false;
        var parts = token.Split('_', 4);
        return parts.Length == 4 && parts[0] == "oc" && Guid.TryParseExact(parts[2], "N", out id) &&
            parts[3].Length >= 40 && parts[3].Length <= 48;
    }

    private static bool HasExpectedEnvironmentTag(string token, string environment) =>
        token.StartsWith($"oc_{EnvironmentTag(environment)}_", StringComparison.Ordinal);

    private static CloudCredentialSummary ToSummary(CloudCredential credential) => new(
        credential.Id, credential.TenantId, credential.ApplicationId, credential.Environment, credential.Fingerprint,
        JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? [], credential.IssuedAt, credential.ExpiresAt,
        credential.RevokedAt, credential.LastUsedAt);

    private static Task<string?> FindRoleAsync(
        CloudControlPlaneDbContext db, CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
        db.Memberships.AsNoTracking().Where(x => x.TenantId == tenantId &&
                x.SubjectIssuer == subject.Issuer && x.SubjectId == subject.SubjectId)
            .Select(x => x.Role).SingleOrDefaultAsync(cancellationToken);

    private static CloudAuditEvent CreateAudit(CloudSubject subject, Guid tenantId, string role, string action, Guid credentialId, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TenantId = tenantId, SubjectIssuer = subject.Issuer, SubjectId = subject.SubjectId,
        ActorRole = role, Action = action, ResourceId = credentialId.ToString("D"), Outcome = "succeeded", OccurredAt = now,
    };
}
