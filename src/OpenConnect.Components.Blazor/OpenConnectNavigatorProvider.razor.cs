using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenConnect.Client;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Client-side Navigator host component placed inside <see cref="OpenConnectProvider"/>
/// alongside <see cref="MiniComposer"/> (per registration spec §3.3).
/// Owns highlight and guided-tour projections and enforces consent-gated actions.
/// </summary>
public partial class OpenConnectNavigatorProvider : ComponentBase, IDisposable
{
    private bool _disposed;

    [Inject] private IServiceProvider ServiceProvider { get; set; } = default!;

    [CascadingParameter] private INavigatorClientService? ParentNavigator { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    public INavigatorClientService Navigator { get; private set; } = default!;

    protected override void OnInitialized()
    {
        if (ParentNavigator is not null)
        {
            throw new InvalidOperationException("Nested OpenConnectNavigatorProvider instances are not permitted.");
        }

        Navigator = ServiceProvider.GetService<INavigatorClientService>()
            ?? new NavigatorClientService(new NavigatorClientOptions());
        Navigator.Changed += OnNavigatorChanged;
    }

    private void OnNavigatorChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (Navigator is not null)
        {
            Navigator.Changed -= OnNavigatorChanged;
            Navigator.StopGuidedTour();
            Navigator.ClearHighlight();
        }

        GC.SuppressFinalize(this);
    }
}
