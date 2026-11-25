using System.Net;
using System.Net.Sockets;

namespace OpenConnect.Hosting.AspNetCore.Fencing;

/// <summary>Resolves DNS hostnames for service fencing validation and socket connection.</summary>
public interface IFencedHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>Default system DNS resolver for service fencing.</summary>
public sealed class SystemFencedHostAddressResolver : IFencedHostAddressResolver
{
    public static SystemFencedHostAddressResolver Instance { get; } = new();

    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Validates outbound service destinations and resolved IP addresses against host fencing policy.</summary>
internal static class ServiceFencingValidator
{
    private static readonly IPAddress AwsIpv6Metadata = IPAddress.Parse("fd00:ec2::254");

    public static string? ValidateContext(ServiceFencingExecutionContext? context, string expectedServiceId, FencedServicePolicy policy)
    {
        if (context is null || context.TenantId.Value == default || context.PrincipalId.Value == default)
            return ServiceFencingReasonCodes.MissingExecutionContext;

        if (!string.Equals(context.ServiceId, expectedServiceId, StringComparison.Ordinal))
            return ServiceFencingReasonCodes.ServiceNotRegistered;

        if (!policy.AllowAnyAuthenticatedTenant && !policy.AllowedTenants.Contains(context.TenantId))
            return ServiceFencingReasonCodes.TenantMismatch;

        if (policy.AllowedPrincipals.Count > 0 && !policy.AllowedPrincipals.Contains(context.PrincipalId))
            return ServiceFencingReasonCodes.PrincipalMismatch;

        if (policy.AllowedApplications.Count > 0 &&
            (string.IsNullOrWhiteSpace(context.ApplicationId) || !policy.AllowedApplications.Contains(context.ApplicationId)))
            return ServiceFencingReasonCodes.ApplicationMismatch;

        if (policy.AllowedPurposes.Count > 0 &&
            (string.IsNullOrWhiteSpace(context.Purpose) || !policy.AllowedPurposes.Contains(context.Purpose)))
            return ServiceFencingReasonCodes.PurposeNotAllowed;

        return null;
    }

    public static string? ValidateRequestDestination(Uri? uri, HttpMethod method, FencedServicePolicy policy)
    {
        if (uri is null || !uri.IsAbsoluteUri)
            return ServiceFencingReasonCodes.InvalidUri;

        if (!policy.AllowedMethods.Contains(method.Method))
            return ServiceFencingReasonCodes.MethodNotAllowed;

        if (!string.IsNullOrEmpty(uri.UserInfo))
            return ServiceFencingReasonCodes.UserInfoRejected;

        if (!policy.AllowedSchemes.Contains(uri.Scheme))
            return ServiceFencingReasonCodes.SchemeNotAllowed;

        var normalizedAllowedHost = NormalizeHost(policy.AllowedHost);
        var normalizedTargetHost = NormalizeHost(uri.IdnHost);
        if (string.IsNullOrEmpty(normalizedAllowedHost) ||
            !string.Equals(normalizedAllowedHost, normalizedTargetHost, StringComparison.OrdinalIgnoreCase))
            return ServiceFencingReasonCodes.HostNotAllowed;

        if (!policy.AllowedPorts.Contains(uri.Port))
            return ServiceFencingReasonCodes.PortNotAllowed;

        return null;
    }

    public static async Task<(IPAddress[]? Addresses, string? DenialReason)> ResolveAndValidateAddressesAsync(
        string host,
        bool allowPrivateNetwork,
        IFencedHostAddressResolver resolver,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await resolver.ResolveAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return (null, ServiceFencingReasonCodes.DnsResolutionFailed);
            }
        }

        if (addresses.Length == 0)
            return (null, ServiceFencingReasonCodes.DnsResolutionFailed);

        foreach (var address in addresses)
        {
            var reason = ValidateAddress(address, allowPrivateNetwork);
            if (reason is not null)
                return (null, reason);
        }

        return (addresses, null);
    }

    public static string? ValidateAddress(IPAddress address, bool allowPrivateNetwork)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
            return ValidateIpv4(address, allowPrivateNetwork);

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return ValidateIpv6(address, allowPrivateNetwork);

        return ServiceFencingReasonCodes.PrivateIpBlocked;
    }

    private static string? ValidateIpv4(IPAddress address, bool allowPrivateNetwork)
    {
        var b = address.GetAddressBytes();

        // Link-local (169.254.0.0/16, includes AWS/GCP/Azure IMDS 169.254.169.254) and Alibaba IMDS (100.100.100.200)
        // are ALWAYS blocked even when private networking is approved.
        if ((b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] == 100 && b[2] == 100 && b[3] == 200))
            return ServiceFencingReasonCodes.MetadataIpBlocked;

        // Unspecified (0.0.0.0/8) and multicast/reserved (>= 224.0.0.0) are always blocked.
        if (b[0] == 0 || b[0] >= 224)
            return ServiceFencingReasonCodes.PrivateIpBlocked;

        var isPrivateOrLoopback =
            IPAddress.IsLoopback(address) ||
            b[0] == 127 ||
            b[0] == 10 ||
            (b[0] == 172 && b[1] is >= 16 and <= 31) ||
            (b[0] == 192 && b[1] == 168) ||
            (b[0] == 100 && b[1] is >= 64 and <= 127);

        if (isPrivateOrLoopback && !allowPrivateNetwork)
            return ServiceFencingReasonCodes.PrivateIpBlocked;

        return null;
    }

    private static string? ValidateIpv6(IPAddress address, bool allowPrivateNetwork)
    {
        if (address.Equals(AwsIpv6Metadata) || address.IsIPv6LinkLocal)
            return ServiceFencingReasonCodes.MetadataIpBlocked;

        var bytes = address.GetAddressBytes();

        // NAT64 well-known prefix 64:ff9b::/96 embeds an IPv4 address in the last 4 bytes.
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b &&
            bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 0 &&
            bytes[8] == 0 && bytes[9] == 0 && bytes[10] == 0 && bytes[11] == 0)
        {
            var embeddedIpv4 = new IPAddress(bytes.AsSpan(12, 4));
            return ValidateIpv4(embeddedIpv4, allowPrivateNetwork);
        }

        // Unspecified (::), IPv6None, multicast are always blocked.
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) || address.IsIPv6Multicast)
            return ServiceFencingReasonCodes.PrivateIpBlocked;

        // Link-local fe80::/10 double check.
        if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80)
            return ServiceFencingReasonCodes.MetadataIpBlocked;

        var isPrivateOrLoopback =
            IPAddress.IsLoopback(address) ||
            address.IsIPv6SiteLocal ||
            bytes[0] == 0xfc ||
            bytes[0] == 0xfd;

        if (isPrivateOrLoopback && !allowPrivateNetwork)
            return ServiceFencingReasonCodes.PrivateIpBlocked;

        return null;
    }

    internal static string NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;
        var trimmed = host.Trim().TrimEnd('.');
        if (Uri.CheckHostName(trimmed) == UriHostNameType.Unknown)
            return string.Empty;
        return Uri.TryCreate($"https://{trimmed}", UriKind.Absolute, out var parsed)
            ? parsed.IdnHost
            : trimmed;
    }

    internal static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        a.Port == b.Port;
}
