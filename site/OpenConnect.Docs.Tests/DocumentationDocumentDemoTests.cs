using Bunit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenConnect.Docs.Services;
using OpenConnect.Docs.Services.DocumentDemo;
using OpenConnect.Docs.Components.DocumentDemo;
using OpenConnect.Documents;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class DocumentationDocumentDemoTests
{
    [Fact]
    public void ConfigurationStringDoesNotExposeCredentials()
    {
        var settings = new DocumentationDocumentConfiguration("/worker.sock", new Uri("https://worker.example"),
            "private-worker-token", "private-inference-token", "private-signing-key", false);
        Assert.DoesNotContain("private-", settings.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExampleIsOptInAndRequiresDevelopmentVisitorIdentity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment);
        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(IDocumentationDocumentDemo));
        Configure(builder);
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment));
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Environment.EnvironmentName = Environments.Production;
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment));
    }

    [Fact]
    public async Task CapacityIsBoundedBeforeTheUploadIsRead()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
        builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var capacity = provider.GetRequiredService<DocumentationDocumentCapacity>();
        await capacity.Slots.WaitAsync();
        await capacity.Slots.WaitAsync();
        try
        {
            using var input = new MemoryStream([1, 2, 3]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IDocumentationDocumentDemo>()
                .ProcessAsync(input, CancellationToken.None));
            Assert.Equal(0, input.Position);
        }
        finally { capacity.Slots.Release(2); }
    }

    [Fact]
    public async Task OversizedUploadIsRejectedBeforeWorkerDispatchAndSlotIsReleased()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
        builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var input = new MemoryStream(new byte[10 * 1024 * 1024 + 1]);
        await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<IDocumentationDocumentDemo>()
            .ProcessAsync(input, CancellationToken.None));
        Assert.Equal(2, provider.GetRequiredService<DocumentationDocumentCapacity>().Slots.CurrentCount);
    }

    [Fact]
    public async Task CancelledUploadReleasesCapacity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        builder.Services.AddScoped<DocumentationGatewayVisitorIdentity>();
        builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment);
        using var provider = builder.Services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        using var input = new WaitingStream();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IDocumentationDocumentDemo>()
            .ProcessAsync(input, cancellation.Token));
        Assert.Equal(2, provider.GetRequiredService<DocumentationDocumentCapacity>().Slots.CurrentCount);
    }

    [Fact]
    public void PageDoesNotExposeWorkerErrorsAndRequiresFileSelection()
    {
        using var context = new BunitContext();
        context.Services.AddSingleton<IDocumentationDocumentDemo>(new FailingDemo());
        var component = context.Render<DocumentationDocumentExample>();
        Assert.True(component.Find("button").HasAttribute("disabled"));
        component.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("synthetic content", "sample.pdf"));
        Assert.False(component.Find("button").HasAttribute("disabled"));
        component.Find("button").Click();
        component.WaitForAssertion(() => Assert.Contains("could not be processed", component.Markup));
        Assert.DoesNotContain("private backend detail", component.Markup);
    }

    [Theory]
    [InlineData("http://worker.example", false)]
    [InlineData("https://user:password@worker.example", false)]
    public void InvalidWorkerTransportIsRejected(string origin, bool allowHttp)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        Configure(builder);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OpenConnect:DocumentDemo:ExtractionOrigin"] = origin,
            ["OpenConnect:DocumentDemo:AllowInsecureHttp"] = allowHttp.ToString()
        });
        builder.Services.AddSingleton(new DocumentationGatewayVisitorTokens("01ARZ3NDEKTSV4RRFFQ69G5FAV", Convert.ToBase64String(new byte[32])));
        Assert.Throws<InvalidOperationException>(() => builder.Services.AddDocumentationDocumentDemo(builder.Configuration, builder.Environment));
    }

    private static void Configure(WebApplicationBuilder builder) => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["OpenConnect:DocumentDemo:Enabled"] = "true",
        ["OpenConnect:DocumentDemo:DocumentSocket"] = Path.GetFullPath("worker.sock"),
        ["OpenConnect:DocumentDemo:ExtractionOrigin"] = "https://worker.example",
        ["OPENCONNECT_WORKER_TOKEN"] = "fixture-token",
        ["OPENCONNECT_WORKER_SIGNING_KEY"] = new string('a', 64)
    });

    private sealed class FailingDemo : IDocumentationDocumentDemo
    {
        public Task<DocumentExtractionResult> ProcessAsync(Stream input, CancellationToken cancellationToken) =>
            Task.FromException<DocumentExtractionResult>(new InvalidOperationException("private backend detail"));
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
