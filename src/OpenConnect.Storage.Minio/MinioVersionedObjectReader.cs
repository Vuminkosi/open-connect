using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Minio;
using Minio.DataModel.Args;

namespace OpenConnect.Storage.Minio;

/// <summary>Reads one exact version from a host-configured private MinIO bucket.</summary>
public sealed class MinioVersionedObjectReader
{
    private const int MaximumBytes = 10 * 1024 * 1024;
    private static readonly Regex ObjectKey = new(@"\Av1/tenants/[0-9a-f]{64}/documents/[0-9a-f]{64}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly IMinioClient _client;
    private readonly string _bucket;

    /// <summary>The caller configures protected transport, scoped credentials and a private bucket.</summary>
    public MinioVersionedObjectReader(IMinioClient client, string bucket)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrWhiteSpace(bucket) || bucket.Length is < 3 or > 63 ||
            bucket.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '.')))
            throw new ArgumentException("Invalid private bucket name.", nameof(bucket));
        _client = client;
        _bucket = bucket;
    }

    /// <summary>Returns a bounded stream for the exact immutable version; the caller must dispose it.</summary>
    public async Task<Stream> OpenVersionAsync(string objectKey, string version, CancellationToken cancellationToken = default)
    {
        if (objectKey is null || !ObjectKey.IsMatch(objectKey) || string.IsNullOrEmpty(version) ||
            version == "null" || version.Length > 1024 || version.Any(c => c < 32 || c == 127))
            throw new ArgumentException("Invalid versioned object reference.");
        try
        { _ = new UTF8Encoding(false, true).GetByteCount(version); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid versioned object reference."); }
        var buffer = new ClearingMemoryStream();
        var copied = false;
        try
        {
            var args = new GetObjectArgs().WithBucket(_bucket).WithObject(objectKey).WithVersionId(version)
                .WithCallbackStream(async (source, token) =>
                {
                    var chunk = new byte[81920];
                    try
                    {
                        int count;
                        while ((count = await source.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
                        {
                            if (buffer.Length + count > MaximumBytes)
                                throw new InvalidOperationException("MinIO object is too large.");
                            await buffer.WriteAsync(chunk.AsMemory(0, count), token).ConfigureAwait(false);
                        }
                        copied = true;
                    }
                    finally { CryptographicOperations.ZeroMemory(chunk); }
                });
            var stat = await _client.GetObjectAsync(args, cancellationToken).ConfigureAwait(false);
            if (!copied || stat is null || stat.DeleteMarker || stat.VersionId != version || stat.Size != buffer.Length)
                throw new InvalidOperationException("MinIO object is unavailable.");
            buffer.Position = 0;
            return buffer;
        }
        catch (OperationCanceledException) { await buffer.DisposeAsync().ConfigureAwait(false); throw; }
        catch (Exception)
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("MinIO object is unavailable.");
        }
    }

    private sealed class ClearingMemoryStream : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing && TryGetBuffer(out var buffer))
                CryptographicOperations.ZeroMemory(buffer.AsSpan());
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
