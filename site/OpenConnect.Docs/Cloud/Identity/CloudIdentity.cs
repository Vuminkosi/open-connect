using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace OpenConnect.Docs.Cloud;

public static class CloudIdentity
{
    public const string GoogleScheme = "CloudGoogle";
    public const string GoogleIssuer = "https://accounts.google.com";
    public const string IssuerClaim = "openconnect:cloud:issuer";
    private const string LocalClaimIssuer = "OpenConnect.Cloud";

    public static Task StampIssuerAsync(TokenValidatedContext context, string issuer)
    {
        if (context.Principal?.Identity is not ClaimsIdentity identity)
        {
            context.Fail("Missing authenticated identity.");
            return Task.CompletedTask;
        }
        foreach (var claimsIdentity in context.Principal.Identities)
            foreach (var claim in claimsIdentity.FindAll(IssuerClaim).ToArray())
                claimsIdentity.RemoveClaim(claim);
        identity.AddClaim(new Claim(IssuerClaim, issuer, ClaimValueTypes.String, LocalClaimIssuer));
        return Task.CompletedTask;
    }

    public static Task HandleRemoteFailureAsync(RemoteFailureContext context)
    {
        context.HandleResponse();
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Redirect("/cloud/sign-in?error=authentication_failed");
        return Task.CompletedTask;
    }

    public static string? GetIssuer(ClaimsPrincipal principal) =>
        principal.Claims.FirstOrDefault(claim => claim.Type == IssuerClaim && claim.Issuer == LocalClaimIssuer)?.Value;

    public static bool IsEnabled(ClaimsPrincipal? principal, IConfigurationSection identity)
    {
        if (principal?.Identity?.IsAuthenticated != true) return false;
        var issuer = GetIssuer(principal);
        if (issuer is null)
        {
            var subject = principal.FindFirst(ClaimTypes.NameIdentifier) ?? principal.FindFirst("sub");
            return identity.GetValue<bool>("Enabled") && subject is not null &&
                string.Equals(subject.Issuer, identity["Authority"], StringComparison.Ordinal);
        }
        return (identity.GetValue<bool>("Enabled") && string.Equals(issuer, identity["Authority"], StringComparison.Ordinal)) ||
            (identity.GetValue<bool>("Google:Enabled") && string.Equals(issuer, GoogleIssuer, StringComparison.Ordinal));
    }

    public static bool IsGoogle(ClaimsPrincipal principal) =>
        string.Equals(GetIssuer(principal), GoogleIssuer, StringComparison.Ordinal);
}
