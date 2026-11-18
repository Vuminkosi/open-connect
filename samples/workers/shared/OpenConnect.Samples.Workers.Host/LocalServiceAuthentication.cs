using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using OpenConnect.Abstractions;

namespace OpenConnect.Samples.Workers.Host;

// This identity is for an isolated local service, not an individual user.
internal sealed class LocalServiceIdentity
{
    private readonly byte[] _secret;
    private readonly string _tenant;
    private readonly string _principal;

    private LocalServiceIdentity(byte[] secret, string tenant, string principal)
    {
        _secret = secret;
        _tenant = tenant;
        _principal = principal;
    }

    public static LocalServiceIdentity? FromEnvironment(IHostEnvironment environment)
    {
        var enabled = Environment.GetEnvironmentVariable("OC_LOCAL_SERVICE_AUTH_ENABLED");
        if (string.IsNullOrEmpty(enabled) || enabled == "0")
            return null;
        if (enabled != "1" || !environment.IsDevelopment())
            throw new InvalidOperationException("Local service authentication requires explicit opt-in in the Development environment.");

        var encodedSecret = Environment.GetEnvironmentVariable("OC_LOCAL_SERVICE_AUTH_SECRET");
        var tenant = Environment.GetEnvironmentVariable("OC_LOCAL_SERVICE_AUTH_TENANT_ID");
        var principal = Environment.GetEnvironmentVariable("OC_LOCAL_SERVICE_AUTH_PRINCIPAL_ID");
        if (encodedSecret is null || encodedSecret.Length != 44 ||
            !TryReadIdentity(tenant) || !TryReadIdentity(principal))
            throw new InvalidOperationException("Local service authentication configuration is invalid.");

        byte[] secret;
        try
        {
            secret = Convert.FromBase64String(encodedSecret);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Local service authentication configuration is invalid.");
        }

        if (secret.Length != 32 || Convert.ToBase64String(secret) != encodedSecret)
            throw new InvalidOperationException("Local service authentication configuration is invalid.");

        return new LocalServiceIdentity(secret, Ulid.Parse(tenant!).ToString(), Ulid.Parse(principal!).ToString());
    }

    public AuthenticateResult Authenticate(HttpContext context, string scheme)
    {
        // Restrict even HTTPS to loopback: this sample is not a multi-tenant service.
        if (!IsLoopback(context.Connection.LocalIpAddress) || !IsLoopback(context.Connection.RemoteIpAddress))
            return AuthenticateResult.Fail("Local service authentication is unavailable on this connection.");

        var headers = context.Request.Headers.Authorization;
        if (headers.Count != 1)
            return AuthenticateResult.NoResult();

        var authorization = headers[0];
        const string prefix = "Bearer ";
        if (authorization is null || authorization.Length != prefix.Length + 44 ||
            !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.Fail("Invalid service credential.");

        Span<byte> candidate = stackalloc byte[32];
        if (!Convert.TryFromBase64Chars(authorization.AsSpan(prefix.Length), candidate, out var written) ||
            written != candidate.Length || !CryptographicOperations.FixedTimeEquals(candidate, _secret))
            return AuthenticateResult.Fail("Invalid service credential.");

        var claims = new[]
        {
            new Claim("tenant_id", _tenant),
            new Claim(ClaimTypes.NameIdentifier, _principal)
        };
        return AuthenticateResult.Success(new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity(claims, scheme)), scheme));
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static bool TryReadIdentity(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length != 26)
            return false;
        try
        {
            return Ulid.Parse(value) != default;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
