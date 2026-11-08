using Microsoft.AspNetCore.Components;

namespace OpenConnect.Navigator.Blazor;

/// <summary>
/// Composes the navigator tour state with the existing component tree.
/// Nest inside (or outside) <c>OpenConnectProvider</c> alongside
/// <c>MiniComposer</c>; this provider owns only tour state and renders its
/// highlight/overlay as siblings — it never replaces the composer and never
/// creates a duplicate root provider.
/// </summary>
public partial class NavigatorProvider : ComponentBase, IDisposable
{
    private bool _disposed;

    /// <summary>Shared tour state. Injected scoped; cascaded to descendants.</summary>
    [Inject] public NavigatorTourService Tour { get; set; } = default!;

    /// <summary>
    /// Route/screen scope for target resolution and tour validation.
    /// Targets registered for other routes are not shown here.
    /// </summary>
    [Parameter] public string? RouteId { get; set; }

    /// <summary>
    /// Unregister this route's targets and stop the tour when the provider
    /// is removed (logout/teardown). Defaults to true. Set to false when the
    /// registry lifetime is owned elsewhere.
    /// </summary>
    [Parameter] public bool DisposeOnUnmount { get; set; } = true;

    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void OnInitialized() => Tour.Changed += OnTourChanged;

    private void OnTourChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Tour.Changed -= OnTourChanged;
        if (DisposeOnUnmount)
        {
            // NAV-REM-BLZ-02: dispose stops the tour and unregisters ONLY this
            // route's targets (UnregisterRoute is route-scoped); other routes'
            // same-named targets are untouched.
            if (!string.IsNullOrWhiteSpace(RouteId))
                Tour.UnregisterRoute(RouteId!);
            Tour.Stop();
        }

        GC.SuppressFinalize(this);
    }
}
