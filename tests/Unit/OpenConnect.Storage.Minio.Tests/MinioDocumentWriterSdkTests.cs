using System.Net;
using Minio;
using Xunit;

namespace OpenConnect.Storage.Minio.Tests;

public sealed class MinioDocumentWriterSdkTests
{
    [Fact]
    public async Task UsesSdkToWriteAndReadBackAnExactVersion()
    {
        using var handler = new S3Handler();
        using var http = new HttpClient(handler);
        using var client = new MinioClient().WithEndpoint("localhost:9000")
            .WithCredentials("fixture-access", "fixture-secret").WithRegion("us-east-1").WithHttpClient(http).Build();
        var writer = new MinioDocumentWriter(client, "private-bucket", (_, _) => Task.CompletedTask,
            (_, _) => ValueTask.FromResult(true));

        var result = await writer.StoreAsync("01ARZ3NDEKTSV4RRFFQ69G5FAV", new byte[] { 1, 2, 3 });

        Assert.Equal("version-1", result.VersionId);
        Assert.Equal(new byte[] { 1, 2, 3 }, handler.Stored);
        Assert.Contains(handler.Requests, request => request.Contains("versionId=version-1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuspendedVersioningPreventsUpload()
    {
        using var handler = new S3Handler { Versioning = "Suspended" };
        using var http = new HttpClient(handler);
        using var client = new MinioClient().WithEndpoint("localhost:9000")
            .WithCredentials("fixture-access", "fixture-secret").WithRegion("us-east-1").WithHttpClient(http).Build();
        var writer = new MinioDocumentWriter(client, "private-bucket", (_, _) => Task.CompletedTask,
            (_, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.StoreAsync("01ARZ3NDEKTSV4RRFFQ69G5FAV", new byte[] { 1 }));
        Assert.Null(handler.Stored);
        Assert.DoesNotContain(handler.Requests, request => request.StartsWith("PUT ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChangedReadbackDoesNotReturnAReference()
    {
        using var handler = new S3Handler { TamperRead = true };
        using var http = new HttpClient(handler);
        using var client = new MinioClient().WithEndpoint("localhost:9000")
            .WithCredentials("fixture-access", "fixture-secret").WithRegion("us-east-1").WithHttpClient(http).Build();
        var writer = new MinioDocumentWriter(client, "private-bucket", (_, _) => Task.CompletedTask,
            (_, _) => ValueTask.FromResult(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.StoreAsync("01ARZ3NDEKTSV4RRFFQ69G5FAV", new byte[] { 1, 2, 3 }));
    }

    private sealed class S3Handler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public byte[]? Stored { get; private set; }
        public string Versioning { get; init; } = "Enabled";
        public bool TamperRead { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add($"{request.Method} {uri.PathAndQuery}");
            if (uri.Query.Contains("versioning", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"<VersioningConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Status>{Versioning}</Status></VersioningConfiguration>") };
            if (request.Method == HttpMethod.Put)
            {
                Stored = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                return new HttpResponseMessage(HttpStatusCode.OK) { Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"etag\"") } };
            }
            if (request.Method == HttpMethod.Head || request.Method == HttpMethod.Get)
            {
                var body = TamperRead && request.Method == HttpMethod.Get ? Stored!.Select(value => (byte)(value ^ 1)).ToArray() : Stored!;
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
                response.Content.Headers.ContentLength = Stored!.Length;
                response.Headers.TryAddWithoutValidation("x-amz-version-id", "version-1");
                response.Headers.TryAddWithoutValidation("ETag", "\"etag\"");
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
                return response;
            }
            throw new InvalidOperationException("Unexpected S3 request.");
        }
    }
}
