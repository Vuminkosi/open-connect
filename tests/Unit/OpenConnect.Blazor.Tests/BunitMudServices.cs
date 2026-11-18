using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace OpenConnect.Blazor.Tests;

internal static class BunitMudServices
{
    public static void AddOpenConnectMudServices(this IServiceCollection services) => services.AddMudServices();

    public static void AddPopoverHost(this BunitContext context)
    {
        context.JSInterop.SetupVoid("mudPopover.dispose").SetVoidResult();
        context.JSInterop.Setup<int>("mudpopoverHelper.countProviders").SetResult(1);
        context.Render<MudPopoverProvider>();
    }
}

public class OpenConnectBunitContext : BunitContext, IDisposable
{
    void IDisposable.Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
