using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenConnect.Docs.Cloud;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class CloudGoogleCallbackTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-nonce")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-audience")]
    public async Task GoogleAuthorizationCodeRequiresValidIssuerAudienceAndNonce(string scenario)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var tokenEndpoint = new TokenEndpoint(key) { Scenario = scenario };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Identity:Google:Enabled"] = "true", ["Identity:Google:ClientId"] = "test-client", ["Identity:Google:ClientSecret"] = "test-secret"
        }).Build();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddCloudIdentityProviders(config.GetSection("Identity"));
        builder.Services.PostConfigure<OpenIdConnectOptions>(CloudIdentity.GoogleScheme, options =>
        {
            var metadata = new OpenIdConnectConfiguration { Issuer = CloudIdentity.GoogleIssuer,
                AuthorizationEndpoint = "https://accounts.google.test/authorize", TokenEndpoint = "https://accounts.google.test/token" };
            metadata.SigningKeys.Add(key);
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            options.Backchannel = new HttpClient(tokenEndpoint);
        });
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapCloudIdentityEndpoints(false, true);
        app.MapGet("/test-identity", (HttpContext context) =>
        {
            var subject = CloudSubjectResolver.FromPrincipal(context.User, "https://other.example");
            return subject is null ? Results.Unauthorized() : Results.Ok(new { subject.Issuer, subject.SubjectId });
        }).RequireAuthorization();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(address) };
        var challenge = await browser.GetAsync("/cloud/sign-in/google?returnUrl=%2Fcloud%2Forganizations");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var query = QueryHelpers.ParseQuery(challenge.Headers.Location!.Query);
        tokenEndpoint.Nonce = query["nonce"].ToString();
        tokenEndpoint.Challenge = query["code_challenge"].ToString();
        Assert.Equal("S256", query["code_challenge_method"].ToString());
        var cookies = string.Join("; ", challenge.Headers.GetValues("Set-Cookie").Select(x => x.Split(';')[0]));
        var callback = new HttpRequestMessage(HttpMethod.Get, "/cloud/signin-google?code=test-code&state=" + Uri.EscapeDataString(query["state"].ToString()));
        callback.Headers.Add("Cookie", cookies);
        var result = await browser.SendAsync(callback);
        Assert.True(tokenEndpoint.PkceVerified);
        var setCookies = result.Headers.TryGetValues("Set-Cookie", out var values) ? values : [];
        var authCookie = setCookies.FirstOrDefault(x => x.StartsWith(".AspNetCore.Cookies=", StringComparison.Ordinal) && !x.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        if (scenario != "valid")
        {
            Assert.Null(authCookie);
            return;
        }
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/cloud/organizations", result.Headers.Location?.ToString());
        Assert.NotNull(authCookie);
        var identityRequest = new HttpRequestMessage(HttpMethod.Get, "/test-identity");
        identityRequest.Headers.Add("Cookie", authCookie.Split(';')[0]);
        var identity = await browser.SendAsync(identityRequest);
        Assert.Equal(HttpStatusCode.OK, identity.StatusCode);
        using var body = JsonDocument.Parse(await identity.Content.ReadAsStringAsync());
        Assert.Equal(CloudIdentity.GoogleIssuer, body.RootElement.GetProperty("issuer").GetString());
        Assert.Equal("google-test-subject", body.RootElement.GetProperty("subjectId").GetString());
    }

    private sealed class TokenEndpoint(SecurityKey key) : HttpMessageHandler
    {
        public string Nonce { get; set; } = "";
        public string Challenge { get; set; } = "";
        public string Scenario { get; set; } = "valid";
        public bool PkceVerified { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken));
            var verifier = form["code_verifier"].ToString();
            PkceVerified = verifier.Length > 0 && Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == Challenge &&
                form["code"].ToString() == "test-code" && form["client_id"].ToString() == "test-client";
            var jwt = new JwtSecurityToken(Scenario == "wrong-issuer" ? "https://attacker.example" : CloudIdentity.GoogleIssuer, Scenario == "wrong-audience" ? "other-client" : "test-client",
                [new Claim("sub", "google-test-subject"), new Claim("iat", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64), new Claim("nonce", Scenario == "wrong-nonce" ? "incorrect" : Nonce)],
                DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                { access_token = "test-access-token", token_type = "Bearer", id_token = new JwtSecurityTokenHandler().WriteToken(jwt) }), Encoding.UTF8, "application/json") };
        }
    }
}
