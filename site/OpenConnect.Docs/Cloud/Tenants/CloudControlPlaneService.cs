using System.Security.Claims;

namespace OpenConnect.Docs.Cloud;

public sealed record CloudSubject(string Issuer, string SubjectId);
public sealed record CloudTenantSummary(Guid Id, string Name, string Role, DateTimeOffset CreatedAt);
public sealed record CloudMemberSummary(string Issuer, string SubjectId, string Role, DateTimeOffset CreatedAt, int Revision);
public sealed record CloudApplicationSummary(Guid Id, Guid TenantId, string Name, string Environment, DateTimeOffset CreatedAt);
public sealed record CreateCloudTenantRequest(string? Name);
public sealed record CreateCloudApplicationRequest(string? Name, string? Environment);
public sealed record CloudMemberChangeRequest(string? Issuer, string? SubjectId, string? Role);
public sealed record CloudMemberChangeResult(string Status, int? Revision = null);

public static class CloudSubjectResolver
{
    public static CloudSubject? FromPrincipal(ClaimsPrincipal principal, string? issuer)
    {
        issuer = CloudIdentity.GetIssuer(principal) ?? issuer;
        var subjectId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
        return string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subjectId)
            ? null
            : new CloudSubject(issuer, subjectId);
    }
}

public interface ICloudTenantStore
{
    Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken);
    Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken);
    Task<CloudMemberChangeResult> ChangeMemberAsync(CloudSubject actor, Guid tenantId, CloudSubject target, string? role, int expectedRevision, CancellationToken cancellationToken);
    Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken);
    Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken);
}

public sealed class CloudControlPlaneService(ICloudTenantStore store)
{
    public Task<IReadOnlyList<CloudTenantSummary>> GetTenantsAsync(CloudSubject subject, CancellationToken cancellationToken) =>
        store.GetTenantsAsync(subject, cancellationToken);

    public Task<CloudTenantSummary> CreateTenantAsync(CloudSubject subject, string name, CancellationToken cancellationToken) =>
        store.CreateTenantAsync(subject, ValidateName(name), cancellationToken);

    public Task<IReadOnlyList<CloudMemberSummary>?> GetMembersAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
        store.GetMembersAsync(subject, tenantId, cancellationToken);

    public Task<CloudMemberChangeResult> ChangeMemberAsync(CloudSubject actor, Guid tenantId, CloudMemberChangeRequest request,
        int expectedRevision, bool remove, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Issuer) || request.Issuer.Length > 2048 ||
            string.IsNullOrWhiteSpace(request.SubjectId) || request.SubjectId.Length > 1024 || expectedRevision < 1 ||
            (!remove && request.Role is not ("Owner" or "Administrator" or "SecurityAnalyst" or "Developer" or "BillingReader")))
            return Task.FromResult(new CloudMemberChangeResult("invalid_request"));
        return store.ChangeMemberAsync(actor, tenantId, new CloudSubject(request.Issuer, request.SubjectId),
            remove ? null : request.Role, expectedRevision, cancellationToken);
    }

    public Task<IReadOnlyList<CloudApplicationSummary>?> GetApplicationsAsync(CloudSubject subject, Guid tenantId, CancellationToken cancellationToken) =>
        store.GetApplicationsAsync(subject, tenantId, cancellationToken);

    public Task<(CloudApplicationSummary? Application, bool Forbidden)> CreateApplicationAsync(
        CloudSubject subject, Guid tenantId, string name, string environment, CancellationToken cancellationToken)
    {
        if (environment is not ("Development" or "Staging" or "Production"))
            throw new ArgumentException("Invalid application environment.", nameof(environment));
        return store.CreateApplicationAsync(subject, tenantId, ValidateName(name), environment, cancellationToken);
    }

    private static string ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
            throw new ArgumentException("Name must contain 1 to 200 characters.", nameof(name));
        return name.Trim();
    }
}
