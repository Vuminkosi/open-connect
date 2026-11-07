namespace OpenConnect.Navigator.Blazor;

/// <summary>
/// Human-facing descriptor for one tour-able UI target.
/// Mirrors the framework-neutral <c>NavigatorTarget</c> shape of the
/// TypeScript bridge (<c>packages/navigator/src/targets.ts</c>): a stable
/// id, the route/screen that owns it, and a short human label.
/// </summary>
/// <param name="TargetId">Stable identifier referenced by tour steps.</param>
/// <param name="RouteId">Route/screen that owns this target (route scoping).</param>
/// <param name="Label">Short human label shown in text tours and captions.</param>
/// <param name="Description">Optional plain-language description (human language only).</param>
/// <param name="Revision">Incremented on every re-registration of the same id.</param>
public sealed record NavigatorTarget(
    string TargetId,
    string RouteId,
    string Label,
    string? Description,
    long Revision);
