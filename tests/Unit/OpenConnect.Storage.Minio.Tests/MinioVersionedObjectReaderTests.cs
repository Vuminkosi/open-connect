using Minio;
using Xunit;

namespace OpenConnect.Storage.Minio.Tests;

public sealed class MinioVersionedObjectReaderTests
{
    private static IMinioClient Client() => new MinioClient().WithEndpoint("127.0.0.1:1")
        .WithCredentials("fixture-access", "fixture-secret").Build();

    [Theory]
    [InlineData("other/key", "version")]
    [InlineData("v1/tenants/invalid/documents/invalid", "version")]
    [InlineData("v1/tenants/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/documents/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "null")]
    [InlineData("v1/tenants/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/documents/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "bad\nversion")]
    public async Task InvalidReferencesFailBeforeStorageAccess(string key, string version)
    {
        using var client = Client();
        var reader = new MinioVersionedObjectReader(client, "private-bucket");
        await Assert.ThrowsAsync<ArgumentException>(() => reader.OpenVersionAsync(key, version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("bucket/other")]
    [InlineData("UPPERCASE")]
    public void InvalidBucketFailsAtConfiguration(string bucket)
    {
        using var client = Client();
        Assert.Throws<ArgumentException>(() => new MinioVersionedObjectReader(client, bucket));
    }
}
