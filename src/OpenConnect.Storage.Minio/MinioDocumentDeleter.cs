using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Minio;
using Minio.DataModel.Args;

namespace OpenConnect.Storage.Minio;

/// <summary>Submits an authorized deletion for one exact tenant-scoped object version.</summary>
public sealed class MinioDocumentDeleter
{
    private static readonly Regex TenantPattern = new(@"\A[0-7][0-9A-HJKMNP-TV-Z]{25}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex ObjectPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly Func<string, string, string, CancellationToken, ValueTask<bool>> _authorize;
    private readonly Func<string, string, CancellationToken, Task> _remove;

    /// <summary>The host must authorize the exact version and reconcile dependent stores and shared references.</summary>
    public MinioDocumentDeleter(IMinioClient client, string bucket,
        Func<string, string, string, CancellationToken, ValueTask<bool>> authorizeDeletion)
    {
        ArgumentNullException.ThrowIfNull(client);
        _ = new MinioVersionedObjectReader(client, bucket);
        _authorize = authorizeDeletion ?? throw new ArgumentNullException(nameof(authorizeDeletion));
        _remove = (key, version, token) => client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(bucket).WithObject(key).WithVersionId(version), token);
    }

    internal MinioDocumentDeleter(Func<string, string, string, CancellationToken, ValueTask<bool>> authorize,
        Func<string, string, CancellationToken, Task> remove)
    { _authorize = authorize; _remove = remove; }

    /// <summary>Submits deletion of a specific version; this is not proof of downstream or backup deletion.</summary>
    public async Task DeleteVersionAsync(string tenantId, string objectId, string versionId, CancellationToken cancellationToken = default)
    {
        if (tenantId is null || !TenantPattern.IsMatch(tenantId) || objectId is null || !ObjectPattern.IsMatch(objectId) ||
            string.IsNullOrEmpty(versionId) || versionId == "null" || versionId.Length > 1024 || versionId.Any(c => c < 32 || c == 127))
            throw new ArgumentException("Invalid document version reference.");
        try
        { _ = new UTF8Encoding(false, true).GetByteCount(versionId); }
        catch (EncoderFallbackException) { throw new ArgumentException("Invalid document version reference."); }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        token.ThrowIfCancellationRequested();
        var allowed = await _authorize(tenantId, objectId, versionId, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!allowed)
            throw new UnauthorizedAccessException("Document deletion is not authorized.");
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(tenantId))).ToLowerInvariant();
        var key = $"v1/tenants/{tenantHash}/documents/{objectId}";
        try
        { await _remove(key, versionId, token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { throw new InvalidOperationException("Document storage is unavailable."); }
        token.ThrowIfCancellationRequested();
    }
}
