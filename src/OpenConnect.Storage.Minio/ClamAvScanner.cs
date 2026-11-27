using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace OpenConnect.Storage.Minio;

/// <summary>Scans bounded document bytes using a protected local clamd Unix socket or private internal TCP endpoint.</summary>
public sealed class ClamAvScanner
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private readonly string? _socketPath;
    private readonly string? _host;
    private readonly int _port;
    private readonly TimeSpan _timeout;

    /// <summary>The socket must be owned and protected by the deployment.</summary>
    public ClamAvScanner(string socketPath, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(socketPath) || !Path.IsPathFullyQualified(socketPath) ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Invalid malware scanner configuration.");
        _socketPath = socketPath;
        _timeout = timeout;
    }

    /// <summary>Connects to an internal, deployment-protected ClamAV TCP endpoint.</summary>
    public ClamAvScanner(string host, int port, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(char.IsWhiteSpace) || port is <= 0 or > 65535 ||
            timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("Invalid malware scanner configuration.");
        _host = host;
        _port = port;
        _timeout = timeout;
    }

    /// <summary>Returns only for a clean verdict; malware and scanner errors fail closed.</summary>
    public async Task ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty || content.Length > MaximumBytes)
            throw new ArgumentException("Invalid document.", nameof(content));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await using var stream = await ConnectAsync(deadline.Token).ConfigureAwait(false);
            await ScanStreamAsync(stream, content, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException("Malware scan failed or was unavailable."); }
    }

    /// <summary>
    /// Scans content and returns a structured, SHA-256-bound receipt distinguishing clean, detected,
    /// limit_exceeded, and unavailable outcomes without leaking raw payloads.
    /// </summary>
    public async Task<MalwareScanReceipt> ScanWithReceiptAsync(
        ReadOnlyMemory<byte> content,
        string policyRevision,
        TimeSpan receiptTtl,
        string? requestId = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policyRevision);
        if (receiptTtl <= TimeSpan.Zero || receiptTtl > TimeSpan.FromHours(24))
            throw new ArgumentOutOfRangeException(nameof(receiptTtl), "Receipt TTL must be between 1 tick and 24 hours.");

        var clock = timeProvider ?? TimeProvider.System;
        var now = clock.GetUtcNow();
        var effectiveRequestId = string.IsNullOrWhiteSpace(requestId) ? Guid.NewGuid().ToString("D") : requestId;

        if (content.IsEmpty)
        {
            return new MalwareScanReceipt(
                effectiveRequestId, MalwareScanStatus.Unsupported, string.Empty, 0,
                "clamav", null, policyRevision, "empty_content", now, now);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant();
        if (content.Length > MaximumBytes)
        {
            return new MalwareScanReceipt(
                effectiveRequestId, MalwareScanStatus.LimitExceeded, sha256, content.Length,
                "clamav", null, policyRevision, "limit_exceeded", now, now);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await using var stream = await ConnectAsync(deadline.Token).ConfigureAwait(false);
            var (status, reasonCode) = await ScanStreamForVerdictAsync(stream, content, deadline.Token).ConfigureAwait(false);
            var scannedAt = clock.GetUtcNow();
            var expiresAt = status == MalwareScanStatus.Clean ? scannedAt + receiptTtl : scannedAt;
            return new MalwareScanReceipt(
                effectiveRequestId, status, sha256, content.Length,
                "clamav", null, policyRevision, reasonCode, scannedAt, expiresAt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            var failedAt = clock.GetUtcNow();
            return new MalwareScanReceipt(
                effectiveRequestId, MalwareScanStatus.Unavailable, sha256, content.Length,
                "clamav", null, policyRevision, "scanner_unavailable", failedAt, failedAt);
        }
    }

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_socketPath is not null)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await tcp.ConnectAsync(_host!, _port, cancellationToken).ConfigureAwait(false);
            return new NetworkStream(tcp, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    internal static async Task ScanStreamAsync(Stream stream, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var (status, _) = await ScanStreamForVerdictAsync(stream, content, cancellationToken).ConfigureAwait(false);
        if (status != MalwareScanStatus.Clean)
            throw new InvalidOperationException("Malware scan failed or was unavailable.");
    }

    internal static async Task<(MalwareScanStatus Status, string ReasonCode)> ScanStreamForVerdictAsync(
        Stream stream, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        await stream.WriteAsync("zINSTREAM\0"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        var length = new byte[4];
        for (var offset = 0; offset < content.Length; offset += 65536)
        {
            var count = Math.Min(65536, content.Length - offset);
            BinaryPrimitives.WriteInt32BigEndian(length, count);
            await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(content.Slice(offset, count), cancellationToken).ConfigureAwait(false);
        }
        Array.Clear(length);
        await stream.WriteAsync(length, cancellationToken).ConfigureAwait(false);
        var reply = new byte[4097];
        try
        {
            var used = 0;
            while (used < reply.Length)
            {
                var count = await stream.ReadAsync(reply.AsMemory(used, 1), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;
                used += count;
                if (reply[used - 1] == 0)
                    break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return ParseVerdict(reply.AsSpan(0, used));
        }
        finally { CryptographicOperations.ZeroMemory(reply); }
    }

    private static (MalwareScanStatus Status, string ReasonCode) ParseVerdict(ReadOnlySpan<byte> span)
    {
        if (span.SequenceEqual("stream: OK\0"u8))
            return (MalwareScanStatus.Clean, "clean");
        if (span.Length > 1 && span[^1] == 0 && span.StartsWith("stream: "u8) &&
            Encoding.ASCII.GetString(span[..^1]).EndsWith(" FOUND", StringComparison.Ordinal))
            return (MalwareScanStatus.Detected, "malware_detected");
        return (MalwareScanStatus.Unavailable, "malformed_verdict");
    }
}
