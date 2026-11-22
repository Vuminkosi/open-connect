using Xunit;
using System.Net;
using Minio.DataModel.Response;

namespace OpenConnect.Storage.Minio.Tests;

public sealed class MinioDocumentWriterTests
{
    private const string Tenant = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Theory]
    [InlineData(200, "key", 3, true)]
    [InlineData(0, "key", 3, false)]
    [InlineData(403, "key", 3, false)]
    [InlineData(200, "different", 3, false)]
    [InlineData(200, "key", 4, false)]
    public void UploadResponseMustConfirmSuccessAndExpectedObject(int status, string key, long size, bool expected)
    {
        var response = new PutObjectResponse((HttpStatusCode)status, "", new Dictionary<string, string>(), size, key);
        Assert.Equal(expected, MinioDocumentWriter.IsSuccessfulUpload(response, "key", 3));
        Assert.False(MinioDocumentWriter.IsSuccessfulUpload(null, "key", 3));
    }

    [Theory]
    [InlineData("\"abc\"", "abc", true)]
    [InlineData("abc-2", "\"abc-2\"", true)]
    [InlineData("abc", "different", false)]
    [InlineData("abc", "", false)]
    [InlineData("\"\"", "\"\"", false)]
    [InlineData(null, "abc", false)]
    public void UploadedAndObservedEntityTagsMustMatch(string? uploaded, string? observed, bool expected) =>
        Assert.Equal(expected, MinioDocumentWriter.MatchingEntityTags(uploaded, observed));

    [Fact]
    public async Task DeniedRetentionDoesNotScanOrWrite()
    {
        var called = false;
        var writer = new MinioDocumentWriter((_, _) => ValueTask.FromResult(false),
            (_, _) => { called = true; return Task.CompletedTask; },
            (_, _, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => writer.StoreAsync(Tenant, new byte[] { 1 }));
        Assert.False(called);
    }

    [Fact]
    public async Task StoresTheScannedSnapshotAndVerifiesTheExactVersion()
    {
        var input = new byte[] { 1, 2, 3 };
        byte[]? stored = null;
        string? objectKey = null;
        var writer = new MinioDocumentWriter((_, _) => ValueTask.FromResult(true),
            (bytes, _) => { input[0] = 42; Assert.Equal(1, bytes.Span[0]); return Task.CompletedTask; },
            (key, bytes, _) => { objectKey = key; stored = bytes.ToArray(); return Task.FromResult("version-1"); },
            (key, version, _) => { Assert.Equal(objectKey, key); Assert.Equal("version-1", version); return Task.FromResult<Stream>(new MemoryStream(stored!)); });
        var reference = await writer.StoreAsync(Tenant, input);
        Assert.Equal(new byte[] { 1, 2, 3 }, stored);
        Assert.Equal(3, reference.SizeBytes);
        Assert.Equal("version-1", reference.VersionId);
        Assert.EndsWith(reference.ObjectId, objectKey);
    }

    [Fact]
    public async Task ScannerFailureAndChangedStoredBytesNeverReturnAReference()
    {
        var wrote = false;
        var failure = new MinioDocumentWriter((_, _) => ValueTask.FromResult(true),
            (_, _) => throw new InvalidOperationException(),
            (_, _, _) => { wrote = true; return Task.FromResult("v1"); }, (_, _, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<InvalidOperationException>(() => failure.StoreAsync(Tenant, new byte[] { 1 }));
        Assert.False(wrote);
        var tampered = new MinioDocumentWriter((_, _) => ValueTask.FromResult(true),
            (_, _) => Task.CompletedTask, (_, _, _) => Task.FromResult("v1"),
            (_, _, _) => Task.FromResult<Stream>(new MemoryStream(new byte[] { 2 })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => tampered.StoreAsync(Tenant, new byte[] { 1 }));
    }

    [Fact]
    public async Task CancellationAfterAuthorizationPreventsScanningEvenIfCallbackIgnoresToken()
    {
        using var cancellation = new CancellationTokenSource();
        var scanned = false;
        var writer = new MinioDocumentWriter((_, _) => { cancellation.Cancel(); return ValueTask.FromResult(true); },
            (_, _) => { scanned = true; return Task.CompletedTask; },
            (_, _, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.StoreAsync(Tenant, new byte[] { 1 }, cancellation.Token));
        Assert.False(scanned);
    }

    [Fact]
    public async Task CancellationAfterScanPreventsStorageAndClearsTheSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        ReadOnlyMemory<byte> snapshot = default;
        var wrote = false;
        var writer = new MinioDocumentWriter((_, _) => ValueTask.FromResult(true),
            (bytes, _) => { snapshot = bytes; cancellation.Cancel(); return Task.CompletedTask; },
            (_, _, _) => { wrote = true; return Task.FromResult("version-1"); },
            (_, _, _) => throw new InvalidOperationException());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.StoreAsync(Tenant, new byte[] { 1, 2, 3 }, cancellation.Token));
        Assert.False(wrote);
        Assert.Equal(new byte[] { 0, 0, 0 }, snapshot.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StorageFailuresDoNotExposeProviderDetails(bool failRead)
    {
        var writer = new MinioDocumentWriter((_, _) => ValueTask.FromResult(true), (_, _) => Task.CompletedTask,
            (_, _, _) => failRead ? Task.FromResult("version") : throw new InvalidOperationException("private-provider-secret"),
            (_, _, _) => throw new InvalidOperationException("private-provider-secret"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => writer.StoreAsync(Tenant, new byte[] { 1 }));
        Assert.Equal("Document storage is unavailable.", error.Message);
        Assert.DoesNotContain("private-provider-secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }
}
