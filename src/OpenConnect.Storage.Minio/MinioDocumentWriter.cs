using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Minio;
using Minio.DataModel.Args;
using Minio.DataModel.Response;

namespace OpenConnect.Storage.Minio;

/// <summary>An immutable, tenant-scoped document reference.</summary>
public sealed class MinioDocumentReference
{
    internal MinioDocumentReference(string objectId, string sha256, int sizeBytes, string versionId)
    { ObjectId = objectId; Sha256 = sha256; SizeBytes = sizeBytes; VersionId = versionId; }
    public string ObjectId { get; }
    public string Sha256 { get; }
    public int SizeBytes { get; }
    public string VersionId { get; }
}

/// <summary>Stores explicitly authorized documents after a local ClamAV scan.</summary>
public sealed class MinioDocumentWriter
{
    private static readonly Regex TenantPattern = new(@"\A[0-7][0-9A-HJKMNP-TV-Z]{25}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Func<string, CancellationToken, ValueTask<bool>> _authorize;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> _scan;
    private readonly Func<string, byte[], CancellationToken, Task<string>> _write;
    private readonly Func<string, string, CancellationToken, Task<Stream>> _read;

    /// <summary>The host must authorize retention for the authenticated operation and configure a private, protected bucket.</summary>
    public MinioDocumentWriter(IMinioClient client, string bucket, ClamAvScanner scanner,
        Func<string, CancellationToken, ValueTask<bool>> authorizeRetention)
        : this(client, bucket, (scanner ?? throw new ArgumentNullException(nameof(scanner))).ScanAsync, authorizeRetention)
    { }

    internal MinioDocumentWriter(IMinioClient client, string bucket,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> scan,
        Func<string, CancellationToken, ValueTask<bool>> authorizeRetention)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(scan);
        _authorize = authorizeRetention ?? throw new ArgumentNullException(nameof(authorizeRetention));
        var reader = new MinioVersionedObjectReader(client, bucket);
        _read = reader.OpenVersionAsync;
        _scan = scan;
        _write = async (key, bytes, token) =>
        {
            var versioning = await client.GetVersioningAsync(new GetVersioningArgs().WithBucket(bucket), token).ConfigureAwait(false);
            if (versioning?.Status != "Enabled")
                throw new InvalidOperationException("Document storage is unavailable.");
            using var stream = new MemoryStream(bytes, writable: false);
            var upload = await client.PutObjectAsync(new PutObjectArgs().WithBucket(bucket).WithObject(key).WithStreamData(stream)
                .WithObjectSize(bytes.Length).WithContentType("application/octet-stream"), token).ConfigureAwait(false);
            if (!IsSuccessfulUpload(upload, key, bytes.Length))
                throw new InvalidOperationException("Document storage is unavailable.");
            var stat = await client.StatObjectAsync(new StatObjectArgs().WithBucket(bucket).WithObject(key), token).ConfigureAwait(false);
            if (stat is null || stat.DeleteMarker || stat.Size != bytes.Length || string.IsNullOrEmpty(stat.VersionId) || stat.VersionId == "null" ||
                !MatchingEntityTags(upload.Etag, stat.ETag))
                throw new InvalidOperationException("Document storage is unavailable.");
            return stat.VersionId;
        };
    }

    internal static bool IsSuccessfulUpload(PutObjectResponse? response, string key, int size) =>
        response is not null && (int)response.ResponseStatusCode is >= 200 and < 300 &&
        response.ObjectName == key && response.Size == size;

    internal static bool MatchingEntityTags(string? uploadTag, string? statTag)
    {
        var uploaded = uploadTag?.Trim('"');
        var observed = statTag?.Trim('"');
        return !string.IsNullOrWhiteSpace(uploaded) && !string.IsNullOrWhiteSpace(observed) &&
            string.Equals(uploaded, observed, StringComparison.Ordinal);
    }

    internal MinioDocumentWriter(Func<string, CancellationToken, ValueTask<bool>> authorize,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> scan, Func<string, byte[], CancellationToken, Task<string>> write,
        Func<string, string, CancellationToken, Task<Stream>> read)
    { _authorize = authorize; _scan = scan; _write = write; _read = read; }

    /// <summary>Stores one scanned snapshot; authorization and retention policy remain the host's responsibility.</summary>
    public async Task<MinioDocumentReference> StoreAsync(string tenantId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (tenantId is null || !TenantPattern.IsMatch(tenantId) || content.IsEmpty || content.Length > 10 * 1024 * 1024)
            throw new ArgumentException("Invalid document or tenant.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var authorized = await _authorize(tenantId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!authorized)
            throw new UnauthorizedAccessException("Document retention is not authorized.");
        var bytes = content.ToArray();
        try
        {
            await _scan(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var digest = SHA256.HashData(bytes);
            var objectId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(tenantId))).ToLowerInvariant();
            var key = $"v1/tenants/{tenantHash}/documents/{objectId}";
            try
            {
                var version = await _write(key, bytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                await using var verified = await _read(key, version, token).ConfigureAwait(false);
                var readDigest = await SHA256.HashDataAsync(verified, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (verified.Length != bytes.Length || !CryptographicOperations.FixedTimeEquals(digest, readDigest))
                    throw new InvalidOperationException("Document storage verification failed.");
                return new MinioDocumentReference(objectId, Convert.ToHexString(digest).ToLowerInvariant(), bytes.Length, version);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch { throw new InvalidOperationException("Document storage is unavailable."); }
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}
