namespace OpenConnect.Client.Cloud;

public enum CloudRoutingMode
{
    CloudOnly = 0,
    LocalOnly = 1,
    Hybrid = 2,
    Automatic = 3,
    CloudFirst = 4,
    BrowserThenCloud = 5,
    LocalFirst = 6,
}

public enum CloudAuthenticationMode
{
    ApiKey = 0,
    WorkloadCredential = 1,
}

public sealed class OpenConnectCloudRoutingOptions
{
    public CloudRoutingMode Default { get; set; } = CloudRoutingMode.LocalOnly;
    public CloudRoutingMode LiveUI { get; set; } = CloudRoutingMode.LocalOnly;
    public CloudRoutingMode Security { get; set; } = CloudRoutingMode.LocalOnly;
    public CloudRoutingMode Inference { get; set; } = CloudRoutingMode.LocalOnly;
    public CloudRoutingMode Voice { get; set; } = CloudRoutingMode.LocalOnly;
}

public sealed class OpenConnectCloudOptions
{
    private const int MaxApiKeyLength = 512;
    private static readonly TimeSpan MaxRequestTimeout = TimeSpan.FromMinutes(5);

    public Uri? Endpoint { get; set; }
    public CloudAuthenticationMode Authentication { get; set; } = CloudAuthenticationMode.ApiKey;
    public string? ApiKey { get; set; }
    public OpenConnectCloudRoutingOptions Routing { get; } = new();
    public bool RequirePrivacyEgressApproval { get; set; } = true;
    public bool AllowInsecureLoopbackHttp { get; set; }
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public void Validate()
    {
        if (Endpoint is null || !Endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The OpenConnect Cloud endpoint must be an absolute URI.", nameof(Endpoint));
        }

        if (!string.IsNullOrEmpty(Endpoint.UserInfo))
        {
            throw new ArgumentException("The OpenConnect Cloud endpoint must not contain user info.", nameof(Endpoint));
        }

        if (!string.IsNullOrEmpty(Endpoint.Query) || !string.IsNullOrEmpty(Endpoint.Fragment))
        {
            throw new ArgumentException("The OpenConnect Cloud endpoint must not contain a query string or fragment.", nameof(Endpoint));
        }

        var isHttps = string.Equals(Endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isLoopbackHttp = AllowInsecureLoopbackHttp
            && string.Equals(Endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && Endpoint.IsLoopback;

        if (!isHttps && !isLoopbackHttp)
        {
            throw new ArgumentException(
                "The OpenConnect Cloud endpoint must use HTTPS (or loopback HTTP only when AllowInsecureLoopbackHttp is true).",
                nameof(Endpoint));
        }

        if (!Enum.IsDefined(Authentication))
        {
            throw new ArgumentOutOfRangeException(nameof(Authentication), "Cloud authentication mode is not valid.");
        }

        if (Authentication == CloudAuthenticationMode.ApiKey)
        {
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new ArgumentException("An OpenConnect Cloud API key is required when ApiKey authentication is selected.", nameof(ApiKey));
            }

            if (ApiKey.Length > MaxApiKeyLength || ContainsWhitespaceOrControlCharacters(ApiKey))
            {
                throw new ArgumentException("The OpenConnect Cloud API key format is invalid.", nameof(ApiKey));
            }
        }
        else if (ApiKey is not null && (ApiKey.Length > MaxApiKeyLength || ContainsWhitespaceOrControlCharacters(ApiKey)))
        {
            throw new ArgumentException("The OpenConnect Cloud API key format is invalid.", nameof(ApiKey));
        }

        ValidateRoutingMode(Routing.Default, $"{nameof(Routing)}.{nameof(Routing.Default)}");
        ValidateRoutingMode(Routing.LiveUI, $"{nameof(Routing)}.{nameof(Routing.LiveUI)}");
        ValidateRoutingMode(Routing.Security, $"{nameof(Routing)}.{nameof(Routing.Security)}");
        ValidateRoutingMode(Routing.Inference, $"{nameof(Routing)}.{nameof(Routing.Inference)}");
        ValidateRoutingMode(Routing.Voice, $"{nameof(Routing)}.{nameof(Routing.Voice)}");

        if (RequestTimeout <= TimeSpan.Zero || RequestTimeout > MaxRequestTimeout)
        {
            throw new ArgumentOutOfRangeException(
                nameof(RequestTimeout),
                "RequestTimeout must be greater than zero and at most 5 minutes.");
        }
    }

    private static void ValidateRoutingMode(CloudRoutingMode mode, string paramName)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(paramName, "Cloud routing mode is not valid.");
        }
    }

    private static bool ContainsWhitespaceOrControlCharacters(string value)
    {
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch))
            {
                return true;
            }
        }

        return false;
    }
}
