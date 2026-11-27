using System.Net;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace OpenConnect.Docs.Cloud;

public static class CloudIdentityEndpoints
{
    public static void MapCloudIdentityEndpoints(this WebApplication app, bool oidcEnabled, bool googleEnabled)
    {
        app.MapGet("/cloud/sign-in", (HttpContext context, string? returnUrl, string? error) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!oidcEnabled && !googleEnabled)
                return Results.Content(
                    "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/app.css\"><title>Sign-in unavailable | OpenConnect</title></head><body><main class=\"cloud-content\"><h1>Sign-in is unavailable</h1><p role=\"status\">Cloud sign-in has not been set up for this site. You can still browse the public documentation.</p><p><a href=\"/\">Back to documentation</a></p></main></body></html>",
                    "text/html; charset=utf-8",
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var encoded = WebUtility.UrlEncode(CloudReturnUrl(returnUrl));
            var errorMessage = error == "authentication_failed" ? "<p role=\"alert\">Sign-in could not be completed. Please try again.</p>" : "";
            var oidcLink = oidcEnabled ? $"<p><a class=\"button\" href=\"/cloud/sign-in/oidc?returnUrl={encoded}\">Sign in with your identity provider</a></p>" : "";
            var googleLink = googleEnabled ? $"<p><a class=\"button\" href=\"/cloud/sign-in/google?returnUrl={encoded}\">Sign in with Google</a></p>" : "";
            return Results.Content($"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><link rel=\"stylesheet\" href=\"/app.css\"><title>Sign in to OpenConnect Cloud</title></head><body><main class=\"cloud-content\"><h1>Sign in to OpenConnect Cloud</h1>{errorMessage}{oidcLink}{googleLink}<p><a href=\"/\">Back to documentation</a></p></main></body></html>", "text/html; charset=utf-8");
        });
        app.MapGet("/cloud/sign-in/{provider}", (string provider, string? returnUrl) =>
        {
            var scheme = provider switch
            {
                "oidc" when oidcEnabled => OpenIdConnectDefaults.AuthenticationScheme,
                "google" when googleEnabled => CloudIdentity.GoogleScheme,
                _ => null,
            };
            if (scheme is null)
                return Results.NotFound();
            return Results.Challenge(new AuthenticationProperties { RedirectUri = CloudReturnUrl(returnUrl) }, [scheme]);
        });
        app.MapPost("/cloud/sign-out", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                return Results.BadRequest();
            }

            var properties = new AuthenticationProperties { RedirectUri = "/" };
            if (oidcEnabled && !CloudIdentity.IsGoogle(context.User))
                return Results.SignOut(properties, [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
            return Results.SignOut(properties, [CookieAuthenticationDefaults.AuthenticationScheme]);
        }).RequireAuthorization();
    }

    public static string CloudReturnUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (string.Equals(value, "/cloud", StringComparison.Ordinal) || value.StartsWith("/cloud/", StringComparison.Ordinal)) &&
        !value.Contains('\\')
            ? value
            : "/cloud";
}
