using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using OpenConnect.Docs.Services;

namespace OpenConnect.Docs.AssistantHost.Identity;

internal sealed class VisitorAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    DocumentationGatewayVisitorTokens tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DocumentationVisitor";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !tokens.TryValidate(header[7..], out var tenant, out var principal))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new[] { new Claim("tenant_id", tenant), new Claim(ClaimTypes.NameIdentifier, principal) };
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
