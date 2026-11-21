using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace OpenConnect.Privacy;

/// <summary>Submits host privacy evidence to an explicitly configured Cloud authority.</summary>
public sealed class CloudPrivacyEvidenceSink : IHostPrivacyEvidenceSink, IDisposable
{
    private readonly HttpClient _client;
    private readonly Uri _endpoint;
    private readonly string _token;
    private readonly bool _ownsClient;

    /// <summary>Creates an HTTPS client using a host-provided client certificate and application credential.</summary>
    public CloudPrivacyEvidenceSink(Uri authority, X509Certificate2 clientCertificate, string bearerToken)
        : this(CreateClient(authority, clientCertificate, bearerToken), authority, bearerToken, true) { }

    /// <summary>Creates a client that trusts one explicitly pinned server certificate while enforcing its hostname and validity.</summary>
    public CloudPrivacyEvidenceSink(Uri authority, X509Certificate2 clientCertificate, string bearerToken,
        string serverCertificateSha256)
        : this(CreateClient(authority, clientCertificate, bearerToken, ParsePin(serverCertificateSha256)), authority, bearerToken, true) { }

    internal CloudPrivacyEvidenceSink(HttpClient client, Uri authority, string bearerToken, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(client);
        ValidateConfiguration(authority, bearerToken);
        _client = client;
        _endpoint = new Uri(authority, "/cloud/internal/v1/privacy/evidence");
        _token = bearerToken;
        _ownsClient = ownsClient;
    }

    /// <summary>Returns only after Cloud acknowledges the exact event ID; failures are safe to retry with the same evidence.</summary>
    public async ValueTask RecordAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        try { await RecordCoreAsync(evidence, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("Cloud privacy evidence was not acknowledged."); }
    }

    private async ValueTask RecordCoreAsync(HostPrivacyEvidence evidence, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "openconnect.privacy-evidence.v1",
            eventId = evidence.EventId, tenantId = evidence.TenantId, destination = evidence.Destination.ToString(),
            allowed = evidence.Allowed, action = evidence.Action.ToString(), reasonCode = evidence.ReasonCode, categories = evidence.Categories });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        HttpResponseMessage? response = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            try
            {
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (attempt == 2 || status is not (429 or 502 or 503 or 504)) break;
                response.Dispose();
                response = null;
            }
            catch (HttpRequestException) when (attempt < 2) { }
            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), deadline.Token).ConfigureAwait(false);
        }
        using var result = response ?? throw new InvalidOperationException("Cloud privacy evidence was not acknowledged.");
        if (!result.IsSuccessStatusCode || result.Content.Headers.ContentLength > 1024)
            throw new InvalidOperationException("Cloud privacy evidence was not acknowledged.");
        await using var stream = await result.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[1025];
        int count;
        while ((count = await stream.ReadAsync(bytes.AsMemory((int)buffer.Length, bytes.Length - (int)buffer.Length), deadline.Token).ConfigureAwait(false)) > 0)
        {
            buffer.Write(bytes, (int)buffer.Length, count);
            if (buffer.Length > 1024) throw new InvalidOperationException("Cloud privacy evidence was not acknowledged.");
        }
        try
        {
            using var acknowledgment = JsonDocument.Parse(buffer.ToArray());
            var root = acknowledgment.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
            var properties = root.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "eventId" ||
                !properties[0].Value.TryGetGuid(out var acknowledged) || acknowledged != evidence.EventId)
                throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidOperationException("Cloud privacy evidence was not acknowledged."); }
    }

    private static void ValidateConfiguration(Uri authority, string bearerToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (!authority.IsAbsoluteUri || authority.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(authority.UserInfo) ||
            !string.IsNullOrEmpty(authority.Query) || !string.IsNullOrEmpty(authority.Fragment) || authority.AbsolutePath != "/" ||
            string.IsNullOrWhiteSpace(bearerToken) || bearerToken.Any(char.IsWhiteSpace))
            throw new ArgumentException("Invalid Cloud privacy evidence configuration.");
    }

    private static HttpClient CreateClient(Uri authority, X509Certificate2 certificate, string bearerToken, byte[]? pin = null)
    {
        ValidateConfiguration(authority, bearerToken);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey) throw new ArgumentException("A client certificate with a private key is required.", nameof(certificate));
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        handler.ClientCertificates.Add(certificate);
        if (pin is not null)
            handler.ServerCertificateCustomValidationCallback = (_, server, chain, errors) =>
                IsPinnedServerCertificate(server, chain, errors, pin, DateTimeOffset.UtcNow);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static byte[] ParsePin(string pin)
    {
        if (pin is null || pin.Length != 64 || !pin.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid Cloud server certificate pin.", nameof(pin));
        return Convert.FromHexString(pin);
    }

    internal static bool IsPinnedServerCertificate(X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors,
        byte[] pin, DateTimeOffset now)
    {
        if (certificate is null || pin.Length != 32 ||
            (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None ||
            now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()) return false;
        if ((errors & SslPolicyErrors.RemoteCertificateChainErrors) != 0 &&
            (chain is null || chain.ChainStatus.Length == 0 || chain.ChainStatus.Any(status =>
                (status.Status & ~(X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0))) return false;
        var usages = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (usages.Length > 1 || usages.Length == 1 && !usages[0].EnhancedKeyUsages.Cast<Oid>().Any(
                oid => oid.Value == "1.3.6.1.5.5.7.3.1")) return false;
        return CryptographicOperations.FixedTimeEquals(certificate.GetCertHash(HashAlgorithmName.SHA256), pin);
    }

    /// <summary>Disposes the owned HTTP client.</summary>
    public void Dispose() { if (_ownsClient) _client.Dispose(); }
}
