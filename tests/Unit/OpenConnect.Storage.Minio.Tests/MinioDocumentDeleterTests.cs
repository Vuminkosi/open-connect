using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace OpenConnect.Storage.Minio.Tests;

public sealed class MinioDocumentDeleterTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly string ObjectId = new('a', 64);

    [Fact]
    public async Task DeletesOnlyTheAuthorizedTenantObjectVersion()
    {
        string? key = null;
        string? selectedVersion = null;
        var deleter = new MinioDocumentDeleter((tenant, objectId, version, _) =>
        {
            Assert.Equal(Tenant, tenant);
            Assert.Equal(ObjectId, objectId);
            Assert.Equal("version-1", version);
            return ValueTask.FromResult(true);
        }, (objectKey, version, _) => { key = objectKey; selectedVersion = version; return Task.CompletedTask; });
        await deleter.DeleteVersionAsync(Tenant, ObjectId, "version-1");
        var tenantHash = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(Tenant))).ToLowerInvariant();
        Assert.Equal($"v1/tenants/{tenantHash}/documents/{ObjectId}", key);
        Assert.Equal("version-1", selectedVersion);
    }

    [Fact]
    public async Task DenialCancellationAndInvalidReferencesNeverDelete()
    {
        var calls = 0;
        var denied = new MinioDocumentDeleter((_, _, _, _) => ValueTask.FromResult(false),
            (_, _, _) => { calls++; return Task.CompletedTask; });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.DeleteVersionAsync(Tenant, ObjectId, "version-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => denied.DeleteVersionAsync(Tenant + "\n", ObjectId, "version-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => denied.DeleteVersionAsync(Tenant, ObjectId, "null"));
        using var cancel = new CancellationTokenSource();
        var cancelled = new MinioDocumentDeleter((_, _, _, _) => { cancel.Cancel(); return ValueTask.FromResult(true); },
            (_, _, _) => { calls++; return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.DeleteVersionAsync(Tenant, ObjectId, "version-1", cancel.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task StorageDiagnosticsAreNotExposed()
    {
        var deleter = new MinioDocumentDeleter((_, _, _, _) => ValueTask.FromResult(true),
            (_, _, _) => throw new InvalidOperationException("private-storage-secret"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => deleter.DeleteVersionAsync(Tenant, ObjectId, "version-1"));
        Assert.DoesNotContain("private-storage-secret", error.ToString(), StringComparison.Ordinal);
    }
}
