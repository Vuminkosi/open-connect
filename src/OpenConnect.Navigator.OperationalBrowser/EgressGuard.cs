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
            !allowedOrigins.Any(origin => SameOrigin(origin, destination))) return null;

        IPAddress[] addresses;
        try { addresses = await resolver.ResolveAsync(destination.DnsSafeHost, cancellationToken).ConfigureAwait(false); }
        catch (SocketException) { return null; }
        return addresses.Length > 0 && addresses.All(IsPublicAddress)
            ? new ValidatedEgressTarget(destination, Array.AsReadOnly(addresses)) : null;
    }

    /// <summary>Checks the initial destination and every redirect target; caller supplies only actual redirect hops.</summary>
    public async Task<IReadOnlyList<ValidatedEgressTarget>?> ValidateRedirectChainAsync(Uri initialDestination,
        IReadOnlyList<Uri> redirectHops, IReadOnlyList<Uri> allowedOrigins, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(redirectHops);
        if (redirectHops.Count > MaxRedirectHops) return null;
        var targets = new List<ValidatedEgressTarget>(redirectHops.Count + 1);
        var current = initialDestination;
        for (var i = 0; i <= redirectHops.Count; i++)
        {
            var validated = await ValidateTargetAsync(current, allowedOrigins, cancellationToken).ConfigureAwait(false);
            if (validated is null) return null;
            targets.Add(validated);
            if (i < redirectHops.Count) current = redirectHops[i];
        }
        return targets.AsReadOnly();
    }

    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == Uri.UriSchemeHttps &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    private static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && !(b[0] == 169 && b[1] == 254) &&
                !(b[0] == 172 && b[1] is >= 16 and <= 31) && !(b[0] == 192 && b[1] == 168) &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) && b[0] < 224;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var bytes = address.GetAddressBytes();
        return !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal && !(bytes[0] == 0xfc || bytes[0] == 0xfd) &&
            !(bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) && !address.Equals(IPAddress.IPv6None) &&
            !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6Multicast;
    }
}
