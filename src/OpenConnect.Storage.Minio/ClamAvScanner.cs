using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace OpenConnect.Storage.Minio;

/// <summary>Scans bounded document bytes using a protected local clamd Unix socket.</summary>
public sealed class ClamAvScanner
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private readonly string _socketPath;
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

    /// <summary>Returns only for a clean verdict; malware and scanner errors fail closed.</summary>
    public async Task ScanAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (content.IsEmpty || content.Length > MaximumBytes) throw new ArgumentException("Invalid document.", nameof(content));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), deadline.Token).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            await ScanStreamAsync(stream, content, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { throw new InvalidOperationException("Malware scan failed or was unavailable."); }
    }

    internal static async Task ScanStreamAsync(Stream stream, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
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
                if (count == 0) break;
                used += count;
                if (reply[used - 1] == 0) break;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!reply.AsSpan(0, used).SequenceEqual("stream: OK\0"u8))
                throw new InvalidOperationException("Malware scan failed or was unavailable.");
        }
        finally { CryptographicOperations.ZeroMemory(reply); }
    }
}
