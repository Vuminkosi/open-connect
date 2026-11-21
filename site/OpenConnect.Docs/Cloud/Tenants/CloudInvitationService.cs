using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Docs.Cloud;

public sealed record CreateCloudInvitationRequest(string? Issuer, string? SubjectId, string? Role);
public sealed record AcceptCloudInvitationRequest(string? Code);
public sealed record CloudInvitationResult(string Status, string? Code = null, DateTimeOffset? ExpiresAt = null);
public sealed record CloudInvitationSummary(Guid Id, string SubjectId, string Role, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt, int Revision);
public sealed record CloudInvitationChangeResult(string Status, int? Revision = null);
public enum CloudInvitationOperation { Create, Revoke, Accept }

public interface ICloudInvitationStore
{
    Task<string> CreateAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string role, Guid id, byte[] secretHash, DateTimeOffset expiresAt, CancellationToken cancellationToken);
    Task<string> AcceptAsync(CloudSubject actor, Guid id, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudInvitationSummary>?> ListAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken);
    Task<CloudInvitationChangeResult> RevokeAsync(CloudSubject actor, Guid tenantId, Guid invitationId, int expectedRevision, CancellationToken cancellationToken);
    Task AuditInvalidAsync(CloudSubject actor, Guid? tenantId, CloudInvitationOperation operation, CancellationToken cancellationToken);
}

public sealed class CloudInvitationService(ICloudInvitationStore store, IConfiguration configuration, TimeProvider timeProvider)
{
    public Task<IReadOnlyList<CloudInvitationSummary>?> ListAsync(CloudSubject actor, Guid tenantId, CancellationToken cancellationToken) =>
        store.ListAsync(actor, tenantId, cancellationToken);

    public async Task<CloudInvitationChangeResult> RevokeAsync(CloudSubject actor, Guid tenantId, Guid invitationId, int expectedRevision, CancellationToken cancellationToken)
    {
        if (expectedRevision >= 1) return await store.RevokeAsync(actor, tenantId, invitationId, expectedRevision, cancellationToken);
        await store.AuditInvalidAsync(actor, tenantId, CloudInvitationOperation.Revoke, cancellationToken);
        return new("invalid_request");
    }
    public async Task<CloudInvitationResult> CreateAsync(CloudSubject actor, Guid tenantId, CreateCloudInvitationRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Issuer) || request.Issuer.Length > 2048 ||
            !((configuration.GetValue<bool>("OpenConnect:Cloud:Identity:Enabled") &&
               string.Equals(request.Issuer, configuration["OpenConnect:Cloud:Identity:Authority"], StringComparison.Ordinal)) ||
              (configuration.GetValue<bool>("OpenConnect:Cloud:Identity:Google:Enabled") &&
               string.Equals(request.Issuer, CloudIdentity.GoogleIssuer, StringComparison.Ordinal))) ||
            string.IsNullOrWhiteSpace(request.SubjectId) || request.SubjectId.Length > 1024 ||
            request.Role is not ("Owner" or "Administrator" or "SecurityAnalyst" or "Developer" or "BillingReader"))
        {
            await store.AuditInvalidAsync(actor, tenantId, CloudInvitationOperation.Create, cancellationToken);
            return new("invalid_request");
        }

        var id = Guid.NewGuid();
        var random = RandomNumberGenerator.GetBytes(32);
        var code = $"oci_{id:N}_{Convert.ToBase64String(random).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        CryptographicOperations.ZeroMemory(random);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(code));
        var expiresAt = timeProvider.GetUtcNow().AddDays(7);
        var status = await store.CreateAsync(actor, tenantId, new CloudSubject(request.Issuer, request.SubjectId), request.Role,
            id, hash, expiresAt, cancellationToken);
        return status == "succeeded" ? new(status, code, expiresAt) : new(status);
    }

    public async Task<string> AcceptAsync(CloudSubject actor, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 120)
        {
            await store.AuditInvalidAsync(actor, null, CloudInvitationOperation.Accept, cancellationToken);
            return "invalid_invitation";
        }
        var parts = code.Split('_', 3);
        if (parts.Length != 3 || parts[0] != "oci" || !Guid.TryParseExact(parts[1], "N", out var id) || parts[2].Length != 43)
        {
            await store.AuditInvalidAsync(actor, null, CloudInvitationOperation.Accept, cancellationToken);
            return "invalid_invitation";
        }
        var bytes = Encoding.UTF8.GetBytes(code);
        var hash = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        try { return await store.AcceptAsync(actor, id, hash, timeProvider.GetUtcNow(), cancellationToken); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }
}
