using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace OpenConnect.Docs.Cloud;

public static class CloudIdentityRegistration
{
    public static IServiceCollection AddCloudIdentityProviders(this IServiceCollection services, IConfigurationSection identity)
    {
        if (identity.GetValue<bool>("Enabled"))
        {
            var authority = identity["Authority"];
            var clientId = identity["ClientId"];
            var clientSecret = identity["ClientSecret"];
            if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException("Cloud identity is enabled but Authority, ClientId, or ClientSecret is missing.");

            services.AddAuthentication().AddOpenIdConnect(options =>
            {
                options.Authority = authority;
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.CallbackPath = "/cloud/signin-oidc";
                options.Events.OnTokenValidated = context => CloudIdentity.StampIssuerAsync(context, authority);
                options.Events.OnRemoteFailure = CloudIdentity.HandleRemoteFailureAsync;
            });
        }

        if (identity.GetValue<bool>("Google:Enabled"))
        {
            var clientId = identity["Google:ClientId"];
            var clientSecret = identity["Google:ClientSecret"];
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
                throw new InvalidOperationException("Google Cloud identity is enabled but ClientId or ClientSecret is missing.");

            services.AddAuthentication().AddOpenIdConnect(CloudIdentity.GoogleScheme, options =>
            {
                options.Authority = CloudIdentity.GoogleIssuer;
                options.ResponseMode = "query";
                options.ClientId = clientId;
                options.ClientSecret = clientSecret;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.GetClaimsFromUserInfoEndpoint = false;
                options.Scope.Clear();
                options.Scope.Add("openid");
                options.Scope.Add("profile");
                options.CallbackPath = "/cloud/signin-google";
                options.Events.OnTokenValidated = context => CloudIdentity.StampIssuerAsync(context, CloudIdentity.GoogleIssuer);
                options.Events.OnRemoteFailure = CloudIdentity.HandleRemoteFailureAsync;
            });
        }

        return services;
    }
}
