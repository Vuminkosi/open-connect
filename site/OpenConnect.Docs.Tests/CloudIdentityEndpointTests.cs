using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudIdentityEndpointTests
{
    [Fact]
    public async Task ProviderSelectorOffersEnabledProvidersAndRestrictsReturnUrl()
    {
        await using var app = CreateApp(oidc: true, google: true);
        await app.StartAsync();
        using var client = Client(app);

        var response = await client.GetAsync("/cloud/sign-in?returnUrl=https%3A%2F%2Fevil.example");
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("Sign in with Google", html);
        Assert.Contains("Sign in with your identity provider", html);
        Assert.Contains("returnUrl=%2Fcloud", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", html);
    }

    [Fact]
    public async Task GenericProviderRemainsAvailableWhenGoogleIsDisabled()
    {
        await using var app = CreateApp(oidc: true, google: false);
        await app.StartAsync();
        using var client = Client(app);

        var selector = await (await client.GetAsync("/cloud/sign-in")).Content.ReadAsStringAsync();
        Assert.Contains("Sign in with your identity provider", selector);
        Assert.DoesNotContain("Sign in with Google", selector);
        var response = await client.GetAsync("/cloud/sign-in/oidc");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("oidc.example", response.Headers.Location!.Host);
        Assert.Contains("code_challenge=", response.Headers.Location.Query);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/cloud/sign-in/google")).StatusCode);
    }

    [Fact]
    public async Task GoogleChallengeUsesAuthorizationCodeAndPkce()
    {
        await using var app = CreateApp(oidc: false, google: true);
        await app.StartAsync();
        using var client = Client(app);

        var response = await client.GetAsync("/cloud/sign-in/google?returnUrl=%2Fcloud");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal("accounts.google.test", location.Host);
        Assert.Contains("response_type=code", location.Query);
        Assert.Contains("code_challenge=", location.Query);
        Assert.Contains("code_challenge_method=S256", location.Query);
        Assert.Contains("state=", location.Query);
        Assert.Contains("nonce=", location.Query);
        Assert.Contains("signin-google", Uri.UnescapeDataString(location.Query));
    }

    [Fact]
    public async Task DisabledProvidersAreUnavailableAndSignOutRequiresAuthentication()
    {
        await using var app = CreateApp(oidc: false, google: false);
        await app.StartAsync();
        using var client = Client(app);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/cloud/sign-in")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/cloud/sign-in/google")).StatusCode);
        var signOut = await client.PostAsync("/cloud/sign-out", null);
        Assert.Equal(HttpStatusCode.Unauthorized, signOut.StatusCode);
    }

    private static WebApplication CreateApp(bool oidc, bool google)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication("Cookies").AddCookie();
        if (oidc)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Enabled"] = "true",
                ["Identity:Authority"] = "https://oidc.example",
                ["Identity:ClientId"] = "client",
                ["Identity:ClientSecret"] = "secret"
            }).Build();
            builder.Services.AddCloudIdentityProviders(configuration.GetSection("Identity"));
            builder.Services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                {
                    Issuer = "https://oidc.example",
                    AuthorizationEndpoint = "https://oidc.example/authorize",
                    TokenEndpoint = "https://oidc.example/token"
                }));
        }
        if (google)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Identity:Google:Enabled"] = "true",
                ["Identity:Google:ClientId"] = "client",
                ["Identity:Google:ClientSecret"] = "secret"
            }).Build();
            builder.Services.AddCloudIdentityProviders(configuration.GetSection("Identity"));
            builder.Services.PostConfigure<OpenIdConnectOptions>(CloudIdentity.GoogleScheme, options =>
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(new OpenIdConnectConfiguration
                {
                    Issuer = CloudIdentity.GoogleIssuer,
                    AuthorizationEndpoint = "https://accounts.google.test/authorize",
                    TokenEndpoint = "https://accounts.google.test/token"
                }));
        }
        builder.Services.AddAuthorization();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapCloudIdentityEndpoints(oidc, google);
        return app;
    }

    private static HttpClient Client(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(addresses.Addresses.Single()) };
    }
}

