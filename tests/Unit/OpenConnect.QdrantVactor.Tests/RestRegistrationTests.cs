using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenConnect.QdrantVactor;
using Xunit;

namespace OpenConnect.QdrantVactor.Tests;

public sealed class RestRegistrationTests
{
    [Fact]
    public void UseQdrantRestVectorStoreResolvesRestStoreWhenConfigured()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQdrantVactor(o =>
        {
            o.Enabled = true;
            o.SubscribeToAgentEvents = false;
            o.Endpoint = new Uri("http://qdrant.test:6333");
            o.ApiKey = "test-key";
        });
        services.UseQdrantRestVectorStore();
        using var provider = services.BuildServiceProvider();

        Assert.IsType<QdrantVectorStoreRest>(provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void UseQdrantRestVectorStoreFailsClosedWithoutApiKey()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddQdrantVactor(o =>
        {
            o.Enabled = true;
            o.SubscribeToAgentEvents = false;
            o.Endpoint = new Uri("http://qdrant.test:6333");
        });
        services.UseQdrantRestVectorStore();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IVectorStore>());
    }

    [Fact]
    public void ValidatorRejectsNonPositiveVectorSize()
    {
        var result = new QdrantVactorOptionsValidator().Validate(null, new QdrantVactorOptions
        {
            Enabled = true, Endpoint = new Uri("http://q:6333"), VectorSize = 0
        });
        Assert.True(result.Failed);
    }
}
