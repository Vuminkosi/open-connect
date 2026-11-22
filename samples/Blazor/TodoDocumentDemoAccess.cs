using System.Security.Claims;
using OpenConnect.Abstractions;
using OpenConnect.Privacy;

namespace OpenConnect.Samples.Blazor;

/// <summary>Limits the optional document example to the configured development identity.</summary>
internal sealed class TodoDocumentDemoAccess
{
    private readonly TenantId _tenant;
    private readonly PrincipalId _principal;

    internal TodoDocumentDemoAccess(IHostEnvironment environment, TodoGatewayOptions options, bool enabled)
    {
        if (!enabled || !environment.IsDevelopment() || options.Mode != TodoGatewayMode.SameHostGateway)
            throw new InvalidOperationException("The document example requires the development-only same-host gateway.");
        _tenant = new TenantId(Ulid.Parse(options.TenantId));
        _principal = new PrincipalId(Ulid.Parse(options.PrincipalId));
        if (_tenant.Value == default || _principal.Value == default)
            throw new InvalidOperationException("The document example identity is invalid.");
    }

    internal EgressContext CreateContext(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated ||
            identity.AuthenticationType != SameHostDevelopmentAuthenticationHandler.SchemeName ||
            identity.FindFirst("tenant_id")?.Value != _tenant.Value.ToString() ||
            identity.FindFirst(ClaimTypes.NameIdentifier)?.Value != _principal.Value.ToString())
            throw new UnauthorizedAccessException("Document example access denied.");
        return new EgressContext(_tenant, _principal, new SessionId(Ulid.NewUlid()), EgressDestination.Worker);
    }

    internal bool Authorizes(EgressContext context) => context.Tenant == _tenant && context.Principal == _principal &&
        context.Session.Value != default && context.Destination == EgressDestination.Worker;
}
