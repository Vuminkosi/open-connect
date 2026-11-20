using System.Text.Json;

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
    ICloudCredentialStore store,
    TimeProvider timeProvider)
{
    private static readonly string[] Administrators = ["Owner", "Administrator"];

    public async Task<(IReadOnlyList<CloudCredentialSummary>? Credentials, bool Forbidden)> ListAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, CancellationToken cancellationToken)
    {
        await using var session = await store.OpenAsync(transactional: false, cancellationToken);
        var role = await session.FindRoleAsync(subject, tenantId, cancellationToken);
        if (role is null) return (null, true);
        if (await session.FindApplicationAsync(tenantId, applicationId, cancellationToken) is null) return (null, true);
        var rows = await session.ListAsync(tenantId, applicationId, cancellationToken);
        var credentials = rows.Select(x => new CloudCredentialSummary(
            x.Id, x.TenantId, x.ApplicationId, x.Environment, x.Fingerprint,
            JsonSerializer.Deserialize<string[]>(x.Scopes) ?? [], x.IssuedAt, x.ExpiresAt, x.RevokedAt, x.LastUsedAt)).ToArray();
        return (credentials, false);
    }

    public async Task<CloudCredentialIssueResult> CreateAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, IReadOnlyList<string>? requestedScopes,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await using var session = await store.OpenAsync(transactional: true, cancellationToken);
        var role = await session.FindRoleAsync(subject, tenantId, cancellationToken);
        if (role is null) return new("not_found");
        if (!Administrators.Contains(role, StringComparer.Ordinal)) return new("forbidden");
        var scopesResult = await ValidateScopesAsync(session, subject, tenantId, applicationId, requestedScopes, cancellationToken);
        if (scopesResult.Status != "succeeded") return new(scopesResult.Status);
        var application = await session.FindApplicationAsync(tenantId, applicationId, cancellationToken);
        if (application is null) return new("not_found");
        if (expiresAt is null || expiresAt <= timeProvider.GetUtcNow()) return new("invalid_expiration");
        var (credential, secret) = NewCredential(tenantId, application, scopesResult.Scopes!, expiresAt, timeProvider.GetUtcNow());
        session.Add(credential);
        session.AddAudit(subject, tenantId, role, "credential.created", credential.Id, timeProvider.GetUtcNow());
        await session.CommitAsync(cancellationToken);
        return new("succeeded", ToSummary(credential), secret);
    }

    public async Task<CloudCredentialIssueResult> RotateAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, Guid credentialId,
        DateTimeOffset? expiresAt, CancellationToken cancellationToken)
    {
        await using var session = await store.OpenAsync(transactional: true, cancellationToken);
        var role = await session.FindRoleAsync(subject, tenantId, cancellationToken);
        if (role is null) return new("not_found");
        if (!Administrators.Contains(role, StringComparer.Ordinal)) return new("forbidden");
        var oldCredential = await session.FindAsync(credentialId, tenantId, applicationId, cancellationToken);
        if (oldCredential is null) return new("not_found");
        if (oldCredential.RevokedAt is not null) return new("credential_revoked");
        if (expiresAt is null || expiresAt <= timeProvider.GetUtcNow()) return new("invalid_expiration");

        var scopes = JsonSerializer.Deserialize<string[]>(oldCredential.Scopes) ?? [];
        var scopesResult = await ValidateScopesAsync(session, subject, tenantId, applicationId, scopes, cancellationToken);
        if (scopesResult.Status != "succeeded") return new(scopesResult.Status);
        var application = await session.FindApplicationAsync(tenantId, applicationId, cancellationToken);
        if (application is null) return new("not_found");

        var now = timeProvider.GetUtcNow();
        var (newCredential, secret) = NewCredential(tenantId, application, scopes, expiresAt, now);
        oldCredential.RevokedAt = now;
        session.Add(newCredential);
        session.AddAudit(subject, tenantId, role, "credential.rotated", oldCredential.Id, now);
        session.AddAudit(subject, tenantId, role, "credential.created_by_rotation", newCredential.Id, now);
        await session.CommitAsync(cancellationToken);
        return new("succeeded", ToSummary(newCredential), secret);
    }

    public async Task<string> RevokeAsync(
        CloudSubject subject, Guid tenantId, Guid applicationId, Guid credentialId, CancellationToken cancellationToken)
    {
        await using var session = await store.OpenAsync(transactional: true, cancellationToken);
        var role = await session.FindRoleAsync(subject, tenantId, cancellationToken);
        if (role is null) return "not_found";
        if (!Administrators.Contains(role, StringComparer.Ordinal)) return "forbidden";
        var credential = await session.FindAsync(credentialId, tenantId, applicationId, cancellationToken);
        if (credential is null) return "not_found";
        if (credential.RevokedAt is not null) return "succeeded";

        var now = timeProvider.GetUtcNow();
        credential.RevokedAt = now;
        session.AddAudit(subject, tenantId, role, "credential.revoked", credentialId, now);
        await session.CommitAsync(cancellationToken);
        return "succeeded";
    }

    public async Task<CloudCredentialIdentity?> ValidateAsync(string? presentedSecret, CancellationToken cancellationToken)
    {
        if (!CloudCredentialSecrets.TryReadId(presentedSecret, out var credentialId)) return null;
        await using var session = await store.OpenAsync(transactional: false, cancellationToken);
        var credential = await session.FindAsync(credentialId, null, null, cancellationToken);
        if (credential is null || credential.RevokedAt is not null ||
            credential.ExpiresAt is null || credential.ExpiresAt <= timeProvider.GetUtcNow() ||
            !CloudCredentialSecrets.HasExpectedEnvironmentTag(presentedSecret!, credential.Environment))
        {
            return null;
        }

        if (!CloudCredentialSecrets.Matches(presentedSecret!, credential.KeyHash)) return null;
        var now = timeProvider.GetUtcNow();
        if (!await session.TouchIfActiveAsync(credential.Id, now, cancellationToken)) return null;
        var scopes = JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? [];
        return new CloudCredentialIdentity(credential.TenantId, credential.ApplicationId, credential.Environment,
            new HashSet<string>(scopes, StringComparer.Ordinal));
    }

    private async Task<(string Status, string[]? Scopes)> ValidateScopesAsync(
        ICloudCredentialSession session, CloudSubject subject, Guid tenantId, Guid applicationId, IReadOnlyList<string>? requestedScopes, CancellationToken cancellationToken)
    {
        if (requestedScopes is null || requestedScopes.Count is < 1 or > 32 ||
            requestedScopes.Any(string.IsNullOrWhiteSpace) || requestedScopes.Distinct(StringComparer.Ordinal).Count() != requestedScopes.Count)
        {
            return ("invalid_scopes", null);
        }

        var snapshot = await session.GetCapabilitiesAsync(subject, tenantId, applicationId, cancellationToken);
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
        Guid tenantId, CloudCredentialApplication application, IReadOnlyList<string> scopes, DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var material = CloudCredentialSecrets.Create(application.Environment);
        var credential = new CloudCredential
        {
            Id = material.Id, TenantId = tenantId, ApplicationId = application.Id, Environment = application.Environment,
            KeyHash = material.Hash, Fingerprint = material.Fingerprint,
            Scopes = JsonSerializer.Serialize(scopes), IssuedAt = now, ExpiresAt = expiresAt?.ToUniversalTime(),
        };
        return (credential, material.Secret);
    }

    private static CloudCredentialSummary ToSummary(CloudCredential credential) => new(
        credential.Id, credential.TenantId, credential.ApplicationId, credential.Environment, credential.Fingerprint,
        JsonSerializer.Deserialize<string[]>(credential.Scopes) ?? [], credential.IssuedAt, credential.ExpiresAt,
        credential.RevokedAt, credential.LastUsedAt);

}
