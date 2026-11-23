using System.Net;
using System.Net.Sockets;

namespace OpenConnect.Navigator.OperationalBrowser;

public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Validates each destination immediately before dispatch; the transport must disable automatic redirects and revalidate each hop.</summary>
public sealed class EgressGuard(IHostAddressResolver resolver)
{
    public const int MaxRedirectHops = 10;

    public async Task<bool> IsAllowedAsync(Uri destination, IReadOnlyList<Uri> allowedOrigins,
        CancellationToken cancellationToken = default)
        => await ValidateTargetAsync(destination, allowedOrigins, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>Validates policy and resolves addresses once; callers must connect only to the returned addresses.</summary>
    public async Task<ValidatedEgressTarget?> ValidateTargetAsync(Uri destination, IReadOnlyList<Uri> allowedOrigins,
        CancellationToken cancellationToken = default)
    {
        if (!destination.IsAbsoluteUri || destination.Scheme != Uri.UriSchemeHttps || destination.UserInfo.Length != 0 ||
            destination.IsDefaultPort is false && destination.Port != 443 ||
            !allowedOrigins.Any(origin => SameOrigin(origin, destination)))
            return null;

        IPAddress[] addresses;
        try
        { addresses = await resolver.ResolveAsync(destination.DnsSafeHost, cancellationToken).ConfigureAwait(false); }
        catch (SocketException) { return null; }
        return addresses.Length > 0 && addresses.All(IsPublicAddress)
            ? new ValidatedEgressTarget(destination, Array.AsReadOnly(addresses)) : null;
    }

    /// <summary>Checks the initial destination and every redirect target; caller supplies only actual redirect hops.</summary>
    public async Task<IReadOnlyList<ValidatedEgressTarget>?> ValidateRedirectChainAsync(Uri initialDestination,
        IReadOnlyList<Uri> redirectHops, IReadOnlyList<Uri> allowedOrigins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectHops);
        if (redirectHops.Count > MaxRedirectHops)
            return null;
        var targets = new List<ValidatedEgressTarget>(redirectHops.Count + 1);
        var current = initialDestination;
        for (var i = 0; i <= redirectHops.Count; i++)
        {
            var validated = await ValidateTargetAsync(current, allowedOrigins, cancellationToken).ConfigureAwait(false);
            if (validated is null)
                return null;
            targets.Add(validated);
            if (i < redirectHops.Count)
                current = redirectHops[i];
        }
        return targets.AsReadOnly();
    }

    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == Uri.UriSchemeHttps &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return IsPublicIPv4(address.GetAddressBytes());
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return false;

        var b = address.GetAddressBytes();
        // ::/96 covers unspecified, loopback and IPv4-compatible addresses (all denied).
        if (b.Take(12).All(x => x == 0))
            return false;
        // NAT64 well-known prefix 64:ff9b::/96 embeds the IPv4 destination in the last 4 bytes.
        if (HasPrefix(b, 0x00, 0x64, 0xff, 0x9b) && b.Skip(4).Take(8).All(x => x == 0))
            return IsPublicIPv4(b[12..16]);
        // 6to4 2002::/16 embeds the IPv4 address in bytes 2-5.
        if (b[0] == 0x20 && b[1] == 0x02)
            return IsPublicIPv4(b[2..6]);
        return !(b[0] == 0x01 && b[1] == 0x00 && b.Skip(2).Take(6).All(x => x == 0)) && // 100::/64 discard
            !HasPrefix(b, 0x00, 0x64, 0xff, 0x9b, 0x00, 0x01) && // 64:ff9b:1::/48 local-use NAT64
            !(b[0] == 0x20 && b[1] == 0x01 && b[2] < 0x02) && // 2001::/23 IETF protocol assignments, incl. Teredo 2001::/32
            !HasPrefix(b, 0x20, 0x01, 0x0d, 0xb8) && // 2001:db8::/32 documentation
            !(b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0) && // 3fff::/20 documentation
            !(b[0] == 0xfc || b[0] == 0xfd) && // fc00::/7 unique local
            !(b[0] == 0xfe && (b[1] & 0xc0) is 0x80 or 0xc0) && // fe80::/10 link-local, fec0::/10 site-local
            b[0] != 0xff; // ff00::/8 multicast
    }

    private static bool HasPrefix(byte[] bytes, params byte[] prefix) => bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool IsPublicIPv4(byte[] b) =>
        b[0] != 0 && b[0] != 10 && b[0] != 127 && !(b[0] == 169 && b[1] == 254) &&
        !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) &&
        !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
        !(b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) && // 192.0.0.0/24, 192.0.2.0/24
        !(b[0] == 198 && b[1] is 18 or 19) && // 198.18.0.0/15
        !(b[0] == 198 && b[1] == 51 && b[2] == 100) && // 198.51.100.0/24
        !(b[0] == 203 && b[1] == 0 && b[2] == 113) && // 203.0.113.0/24
        b[0] < 224; // multicast, reserved 240.0.0.0/4 and broadcast
}
