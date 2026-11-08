namespace OpenConnect.Navigator.Blazor;

/// <summary>
/// One step of an app tour. Text-first: <see cref="Title"/> and
/// <see cref="Body"/> are always rendered (including during static
/// prerendering and when no highlight anchor exists); <see cref="TargetId"/>
/// is an optional anchor hint resolved against registered targets.
/// </summary>
/// <param name="Title">Short step caption (1–120 characters).</param>
/// <param name="Body">Plain-language step copy (1–1000 characters).</param>
/// <param name="TargetId">Optional stable target this step refers to.</param>
/// <param name="RouteId">Optional per-step route scope; defaults to the provider route.</param>
public sealed record NavigatorTourStep(
    string Title,
    string Body,
    string? TargetId = null,
    string? RouteId = null);
