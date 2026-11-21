using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Docs.Components.Pages;
using OpenConnect.Docs.Services;
using Xunit;

namespace OpenConnect.Docs.Tests;

public sealed class ApiReferenceComponentTests : BunitContext
{
    [Fact]
    public void RestAndWorkerContractTargetIsRendered()
    {
        Services.AddSingleton<GeneratedApiStore>();
        var component = Render<ApiIndex>();
        Assert.Equal("REST and worker contracts", component.Find("#api-rest-worker-contracts").TextContent);
    }
}
