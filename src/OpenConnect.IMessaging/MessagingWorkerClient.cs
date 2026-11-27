using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OpenConnect.Abstractions;
using OpenConnect.Abstractions.Messaging;

namespace OpenConnect.IMessaging;

/// <summary>
/// Private authenticated transport client to the OpenConnect IMessaging Python worker.
/// Supports Unix domain sockets and HTTPS with Bearer authentication, HMAC-SHA256 signing
/// over exact serialized bytes, replay nonces, no redirects, and bounded response reading.
/// </summary>
public sealed class MessagingWorkerClient : IMessagingWorkerClient, IDisposable
{
    public const string SignatureHeaderName = "X-OpenConnect-Signature";
    public const string KeyIdHeaderName = "X-OpenConnect-Key-Id";
    private const string ExecuteRelativePath = "/internal/v1/execute";
    private const string CapabilitiesRelativePath = "/internal/v1/capabilities";

    private readonly MessagingWorkerOptions _options;
    private readonly HttpClient? _httpClient;
    private readonly Uri? _baseUri;
    private readonly byte[]? _signingKey;
    private readonly bool _ownsClient;
    private readonly TimeProvider _timeProvider;
    private bool _disposed;

    public MessagingWorkerClient(
        IOptions<MessagingWorkerOptions> options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.Enabled)
        {
            _signingKey = (byte[])_options.SigningKey!.Clone();
            var (isUnixSocket, socketPath, httpBaseUri) = _options.ResolveEndpoint();
            if (isUnixSocket)
            {
                _httpClient = CreateUnixSocketClient(socketPath!, _options.RequestTimeout);
                _baseUri = new Uri("http://localhost/");
            }
            else
            {
                var handler = new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    UseProxy = false
                };
                _httpClient = new HttpClient(handler) { Timeout = _options.RequestTimeout };
                _baseUri = httpBaseUri!;
            }
            _ownsClient = true;
        }
    }

    internal MessagingWorkerClient(
        HttpClient httpClient,
        Uri baseUri,
        MessagingWorkerOptions options,
        TimeProvider? timeProvider = null,
        bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _httpClient = httpClient;
        _baseUri = baseUri;
        _signingKey = options.SigningKey is not null ? (byte[])options.SigningKey.Clone() : null;
        _ownsClient = ownsClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<MessagingWorkerResult> ExecuteAsync(
        MessagingWorkerCommand command,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);

        if (!_options.Enabled || _httpClient is null || _baseUri is null || _signingKey is null)
            throw new MessagingDomainException(ErrorCodes.MessagingDisabled, "IMessaging worker bridge is disabled.", 503);

        ValidateCommand(command);
        cancellationToken.ThrowIfCancellationRequested();

        using var payloadDoc = ParsePayloadObject(command.PayloadJson);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = command.SchemaVersion,
            taskId = command.TaskId,
            kind = command.Kind,
            tenantId = command.TenantId,
            nonce = command.Nonce,
            issuedAt = command.IssuedAt,
            expiresAt = command.ExpiresAt,
            payload = payloadDoc.RootElement
        });

        if (body.Length > _options.MaxRequestBytes)
        {
            CryptographicOperations.ZeroMemory(body);
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Worker command exceeds maximum request size.", 413);
        }

        byte[]? chunk = null;
        MemoryStream? buffer = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);

        try
        {
            var executeUri = new Uri(_baseUri, ExecuteRelativePath);
            using var request = new HttpRequestMessage(HttpMethod.Post, executeUri);
            ApplyAuthHeaders(request, body);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "IMessaging worker is unreachable.", 503, ex);
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "IMessaging worker request timed out.", 503, ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    ThrowForErrorStatus(response.StatusCode);

                if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                    throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response exceeded maximum allowed size.", 502);

                await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                buffer = new MemoryStream();
                chunk = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > _options.MaxResponseBytes)
                        throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker response exceeded maximum allowed size.", 502);
                    buffer.Write(chunk, 0, read);
                }

                buffer.Position = 0;
                try
                {
                    using var doc = JsonDocument.Parse(buffer);
                    return WorkerResponseValidator.ValidateExecuteResponse(
                        doc.RootElement,
                        command.TaskId,
                        command.Kind,
                        command.TenantId);
                }
                catch (JsonException ex)
                {
                    throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, "Worker returned malformed JSON.", 502, ex);
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(body);
            if (chunk is not null)
                CryptographicOperations.ZeroMemory(chunk);
            if (buffer is not null)
            {
                if (buffer.TryGetBuffer(out var seg))
                    CryptographicOperations.ZeroMemory(seg.AsSpan());
                buffer.Dispose();
            }
        }
    }

    public async ValueTask<MessagingWorkerReadiness> CheckReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _timeProvider.GetUtcNow();

        if (!_options.Enabled || _httpClient is null || _baseUri is null || _signingKey is null)
            return new MessagingWorkerReadiness(false, Array.Empty<string>(), "openconnect.v1", now);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.RequestTimeout);

        try
        {
            var uri = new Uri(_baseUri, CapabilitiesRelativePath);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            ApplyAuthHeaders(request, ReadOnlySpan<byte>.Empty);

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return new MessagingWorkerReadiness(false, Array.Empty<string>(), "openconnect.v1", now);

            if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                return new MessagingWorkerReadiness(false, Array.Empty<string>(), "openconnect.v1", now);

            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(chunk, deadline.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > _options.MaxResponseBytes)
                    return new MessagingWorkerReadiness(false, Array.Empty<string>(), "openconnect.v1", now);
                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            using var doc = JsonDocument.Parse(buffer);
            return WorkerResponseValidator.ValidateCapabilitiesResponse(doc.RootElement, now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or MessagingDomainException)
        {
            return new MessagingWorkerReadiness(false, Array.Empty<string>(), "openconnect.v1", now);
        }
    }

    public static string ComputeSignatureHex(byte[] signingKey, ReadOnlySpan<byte> payloadBytes)
    {
        ArgumentNullException.ThrowIfNull(signingKey);
        return Convert.ToHexString(HMACSHA256.HashData(signingKey, payloadBytes)).ToLowerInvariant();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_signingKey is not null)
            CryptographicOperations.ZeroMemory(_signingKey);
        if (_ownsClient)
            _httpClient?.Dispose();
    }

    private void ApplyAuthHeaders(HttpRequestMessage request, ReadOnlySpan<byte> body)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.BearerToken!);
        request.Headers.Add(SignatureHeaderName, ComputeSignatureHex(_signingKey!, body));
        if (!string.IsNullOrWhiteSpace(_options.SigningKeyId))
            request.Headers.Add(KeyIdHeaderName, _options.SigningKeyId);
    }

    private static void ValidateCommand(MessagingWorkerCommand command)
    {
        if (!string.Equals(command.SchemaVersion, "openconnect.v1", StringComparison.Ordinal))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Unsupported worker command schemaVersion.", 400);
        if (string.IsNullOrWhiteSpace(command.TaskId) || string.IsNullOrWhiteSpace(command.TenantId))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Command TaskId and TenantId are required.", 400);
        if (command.Kind is not (MessagingWorkerCommandKinds.Route or MessagingWorkerCommandKinds.Presence
            or MessagingWorkerCommandKinds.Signal or MessagingWorkerCommandKinds.PushDispatch))
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, $"Unsupported messaging worker command kind '{command.Kind}'.", 400);
        }
        if (string.IsNullOrWhiteSpace(command.Nonce) || command.Nonce.Length is < 16 or > 128)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Command nonce must be between 16 and 128 characters.", 400);
        if (command.ExpiresAt <= command.IssuedAt)
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Command ExpiresAt must be after IssuedAt.", 400);
    }

    private static JsonDocument ParsePayloadObject(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Command payload JSON is required.", 400);
        try
        {
            var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                doc.Dispose();
                throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Command payload must be a JSON object.", 400);
            }
            return doc;
        }
        catch (JsonException ex)
        {
            throw new MessagingDomainException(ErrorCodes.InvalidRequest, "Command payload is not valid JSON.", 400, ex);
        }
    }

    private static void ThrowForErrorStatus(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        if (statusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new MessagingDomainException(ErrorCodes.Unauthorized, $"IMessaging worker rejected authentication ({code}).", code);
        if (statusCode is HttpStatusCode.Conflict)
            throw new MessagingDomainException(ErrorCodes.Conflict, "IMessaging worker rejected replayed or conflicting command.", code);
        throw new MessagingDomainException(ErrorCodes.WorkerUnavailable, $"IMessaging worker returned error status {code}.", 503);
    }

    private static HttpClient CreateUnixSocketClient(string socketPath, TimeSpan timeout)
    {
        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { Timeout = timeout };
    }
}
