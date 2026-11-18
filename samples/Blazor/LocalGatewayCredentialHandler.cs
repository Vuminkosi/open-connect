using System.Net;
using System.Net.Http.Headers;

namespace OpenConnect.Samples.Blazor;

// For explicitly configured, isolated local acceptance traffic only.
internal sealed class LocalGatewayCredentialHandler : HttpMessageHandler
{
    private const int MaximumDestinationsPerCredential = 16;
    private readonly Destination[] _guardDestinations;
    private readonly Destination[] _gatewayDestinations;
    private readonly AuthenticationHeaderValue _guardCredential;
    private readonly AuthenticationHeaderValue _gatewayCredential;
    private readonly HttpMessageInvoker _transport;

    private LocalGatewayCredentialHandler(
        IEnumerable<string> guardDestinations,
        string guardBearer,
        IEnumerable<string> gatewayDestinations,
        string gatewayBearer)
    {
        _guardDestinations = ParseDestinations(guardDestinations);
        _gatewayDestinations = ParseDestinations(gatewayDestinations);
        if (_guardDestinations.Any(guard => _gatewayDestinations.Any(guard.Overlaps)))
            throw new ArgumentException("Local credential destinations must be distinct.");

        _guardCredential = CreateCredential(guardBearer);
        _gatewayCredential = CreateCredential(gatewayBearer);
        _transport = new HttpMessageInvoker(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false
        }, disposeHandler: true);
    }

    // Each destination is an exact URL path, not a prefix. Query strings may vary.
    // Supply both bearers from server-side configuration, never from a request.
    public static HttpClient CreateClient(
        string guardDestination,
        string guardBearer,
        string gatewayDestination,
        string gatewayBearer) =>
        CreateClient([guardDestination], guardBearer, [gatewayDestination], gatewayBearer);

    public static HttpClient CreateClient(
        IEnumerable<string> guardDestinations,
        string guardBearer,
        IEnumerable<string> gatewayDestinations,
        string gatewayBearer) =>
        new(new LocalGatewayCredentialHandler(
            guardDestinations, guardBearer, gatewayDestinations, gatewayBearer), disposeHandler: true);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.Contains("Authorization") || request.Headers.Host is not null)
            throw new InvalidOperationException("The local request contains a prohibited header.");

        var destination = Destination.Parse(request.RequestUri);
        AuthenticationHeaderValue credential;
        if (_guardDestinations.Contains(destination))
            credential = _guardCredential;
        else if (_gatewayDestinations.Contains(destination))
            credential = _gatewayCredential;
        else
            throw new InvalidOperationException("The local request destination is not allowed.");

        request.Headers.Authorization = credential;
        try
        {
            return await _transport.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            request.Headers.Authorization = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _transport.Dispose();
        base.Dispose(disposing);
    }

    private static AuthenticationHeaderValue CreateCredential(string bearer)
    {
        if (string.IsNullOrEmpty(bearer) ||
            bearer.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.' and not '~' and not '+' and not '/' and not '='))
            throw new ArgumentException("A valid local bearer credential is required.");

        return new AuthenticationHeaderValue("Bearer", bearer);
    }

    private static Destination[] ParseDestinations(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var destinations = values.Take(MaximumDestinationsPerCredential + 1)
            .Select(Destination.Parse)
            .ToArray();
        if (destinations.Length is 0 or > MaximumDestinationsPerCredential)
            throw new ArgumentException("A bounded, nonempty set of local destinations is required.");

        for (var i = 0; i < destinations.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (destinations[i].Overlaps(destinations[j]))
                    throw new ArgumentException("Local credential destinations must be distinct.");
            }
        }

        return destinations;
    }

    private sealed record Destination(string Scheme, string Authority, IPAddress Address, int Port, string Path)
    {
        public bool Overlaps(Destination other) =>
            Scheme == other.Scheme && Address.Equals(other.Address) && Port == other.Port && Path == other.Path;

        public static Destination Parse(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
                throw new ArgumentException("A valid local destination is required.");
            if (uri.Query.Length != 0)
                throw new ArgumentException("A configured local destination cannot contain a query.");
            return Parse(uri);
        }

        public static Destination Parse(Uri? uri)
        {
            if (uri is null || !uri.IsAbsoluteUri ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                uri.UserInfo.Length != 0 || uri.OriginalString.Contains('#') ||
                uri.OriginalString.Contains('\\') ||
                !TryGetAuthorityAndPath(uri, out var authority, out var originalPath) ||
                !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address))
                throw new ArgumentException("A valid local destination is required.");

            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (!IPAddress.IsLoopback(address) || originalPath.Contains('%') ||
                !string.Equals(originalPath, uri.AbsolutePath, StringComparison.Ordinal))
                throw new ArgumentException("A valid local destination is required.");

            return new Destination(uri.Scheme, authority, address, uri.Port, uri.AbsolutePath);
        }

        private static bool TryGetAuthorityAndPath(Uri uri, out string authority, out string path)
        {
            authority = string.Empty;
            path = string.Empty;
            var original = uri.OriginalString;
            var authorityStart = original.IndexOf("://", StringComparison.Ordinal);
            if (authorityStart < 0)
                return false;
            authorityStart += 3;
            var authorityEnd = original.IndexOfAny(['/', '?', '#'], authorityStart);
            authority = authorityEnd < 0 ? original[authorityStart..] : original[authorityStart..authorityEnd];
            var pathEnd = original.IndexOfAny(['?', '#'], authorityStart);
            pathEnd = pathEnd < 0 ? original.Length : pathEnd;
            path = authorityEnd < 0 || original[authorityEnd] != '/'
                ? "/"
                : original[authorityEnd..pathEnd];
            if (authority.Contains('@'))
                return false;
            var colon = authority.LastIndexOf(':');
            if (colon <= 0 || colon == authority.Length - 1 ||
                !authority[(colon + 1)..].All(char.IsAsciiDigit) ||
                !int.TryParse(authority[(colon + 1)..], out var port) ||
                port is < 1 or > 65535)
                return false;

            var validShape = authority[0] == '['
                ? authority.IndexOf(']') == colon - 1
                : authority.IndexOf(':') == colon;
            if (!validShape)
                return false;

            var host = authority[0] == '[' ? authority[1..(colon - 1)] : authority[..colon];
            return !host.Contains('%') && IPAddress.TryParse(host, out _);
        }
    }
}
