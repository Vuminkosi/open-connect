using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Primitives;

namespace OpenConnect.Samples.Blazor;

// This isolated development guard is not user authentication.
internal sealed class LocalAcceptanceGuard
{
    private readonly RequestDelegate _next;
    private readonly string _origin;
    private readonly HashSet<string> _hosts;
    private readonly byte[] _credential;
    private readonly IServer _server;

    private LocalAcceptanceGuard(RequestDelegate next, string origin, HashSet<string> hosts, byte[] credential, IServer server)
    {
        _next = next;
        _origin = origin;
        _hosts = hosts;
        _credential = credential;
        _server = server;
    }

    public static void UseIfEnabled(WebApplication app)
    {
        const string prefix = "OpenConnect:LocalAcceptance:";
        if (!app.Configuration.GetValue<bool>(prefix + "Enabled"))
            return;
        if (!app.Environment.IsDevelopment())
            throw new InvalidOperationException("Local acceptance mode is only available in Development.");

        var origin = app.Configuration[prefix + "BrowserOrigin"];
        if (origin is null || !IsOrigin(origin))
            throw new InvalidOperationException("A valid local acceptance browser origin is required.");

        var authorities = app.Configuration.GetSection(prefix + "AllowedHosts").Get<string[]>();
        if (authorities is null || authorities.Length == 0 || authorities.Any(host => !IsAuthority(host)))
            throw new InvalidOperationException("Valid local acceptance host authorities with ports are required.");

        var secret = app.Configuration[prefix + "ServiceBearer"];
        // A 32-byte random value encoded as unpadded base64url is required.
        if (secret is null || secret.Length != 43 || secret.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            throw new InvalidOperationException("A valid local acceptance service bearer is required.");
        try
        {
            if (Convert.FromBase64String(secret.Replace('-', '+').Replace('_', '/') + "=").Length != 32)
                throw new FormatException();
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("A valid local acceptance service bearer is required.");
        }

        var credential = SHA256.HashData(Encoding.ASCII.GetBytes(secret));
        var hosts = new HashSet<string>(authorities, StringComparer.OrdinalIgnoreCase);
        var server = app.Services.GetRequiredService<IServer>();
        app.Use(next => new LocalAcceptanceGuard(next, origin, hosts, credential, server).InvokeAsync);
        app.UseRouting();
    }

    private async Task InvokeAsync(HttpContext context)
    {
        if (!ListenersAreLoopback() ||
            !IsLoopback(context.Connection.LocalIpAddress) ||
            !IsLoopback(context.Connection.RemoteIpAddress) ||
            !TrySingle(context.Request.Headers.Host, out var host) ||
            !IsAuthority(host) || !_hosts.Contains(host))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var todoRoute = context.Request.Path.StartsWithSegments("/internal/openconnect/todos");
        var hasGuardHeader = context.Request.Headers.ContainsKey("X-OpenConnect-Local-Guard");
        if (todoRoute)
        {
            if (!hasGuardHeader ||
                !TrySingle(context.Request.Headers["X-OpenConnect-Local-Guard"], out var guardValue) ||
                guardValue.Length != 43 ||
                guardValue.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
                !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.ASCII.GetBytes(guardValue)), _credential))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }
        else if (hasGuardHeader)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var authorization = context.Request.Headers.Authorization;
        var hasAuthorization = context.Request.Headers.ContainsKey("Authorization");
        var validBearer = false;
        if (hasAuthorization && !todoRoute)
        {
            if (!TrySingle(authorization, out var value) ||
                !value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                value.Length != 50)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            var supplied = value[7..];
            if (supplied.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_'))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            validBearer = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.ASCII.GetBytes(supplied)), _credential);
            if (!validBearer)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        var hasOrigin = context.Request.Headers.ContainsKey("Origin");
        if (hasOrigin && (!TrySingle(context.Request.Headers.Origin, out var requestOrigin) ||
            !IsOrigin(requestOrigin) || !string.Equals(requestOrigin, _origin, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var safe = HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method);
        var websocket = context.Request.Headers.Upgrade.Any(value =>
            value is not null && value.Split(',').Any(token => token.Trim().Equals("websocket", StringComparison.OrdinalIgnoreCase)))
            || HttpMethods.IsConnect(context.Request.Method);
        if ((!safe || websocket) && !hasOrigin && !validBearer && !todoRoute)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await _next(context);
    }

    private bool ListenersAreLoopback()
    {
        var addresses = _server.Features.Get<IServerAddressesFeature>()?.Addresses;
        return addresses is { Count: > 0 } && addresses.All(address =>
            Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
             IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IsLoopback(ip)));
    }

    private static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    private static bool TrySingle(StringValues values, out string value)
    {
        value = values.Count == 1 ? values[0] ?? string.Empty : string.Empty;
        return value.Length > 0 && !value.Contains(',') && value == value.Trim();
    }

    private static bool IsAuthority(string? authority)
    {
        if (string.IsNullOrEmpty(authority) || authority.Any(char.IsWhiteSpace) ||
            authority.IndexOfAny(['/', '\\', '?', '#', '@', ',']) >= 0 ||
            !Uri.TryCreate("http://" + authority, UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Host.Length == 0 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return false;

        var lastColon = authority.LastIndexOf(':');
        return lastColon > 0 && int.TryParse(authority[(lastColon + 1)..], out var port) &&
            port is > 0 and <= 65535 && authority[(lastColon + 1)..].All(char.IsAsciiDigit) &&
            (authority[0] == '[' ? authority.IndexOf(']') == lastColon - 1 : authority.IndexOf(':') == lastColon);
    }

    private static bool IsOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Length == 0 || uri.AbsolutePath != "/" ||
            !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            return false;
        return true;
    }
}
