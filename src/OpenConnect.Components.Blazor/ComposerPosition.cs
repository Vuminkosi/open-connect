using OpenConnect.Components.Contracts;

namespace OpenConnect.Components.Blazor;

/// <summary>
/// Persisted drag offset for the floating composer bubble, in CSS pixels,
/// plus static placement constants for <see cref="OpenConnectComposerOptions.Position"/>.
/// Positive <see cref="OffsetX"/> moves the bubble toward the inline-start side;
/// positive <see cref="OffsetY"/> moves it upward. Clamping against the live
/// viewport happens in <c>composer-drag.js</c>; this record only carries the
/// last settled value so Blazor can re-apply it after re-render.
/// </summary>
public sealed record ComposerPosition(double OffsetX, double OffsetY)
{
    public static ComposerPosition Default { get; } = new(0, 0);

    public static ComposerPlacement BottomRight => ComposerPlacement.BottomRight;

    public static ComposerPlacement BottomLeft => ComposerPlacement.BottomLeft;

    public static ComposerPlacement TopRight => ComposerPlacement.TopRight;

    public static ComposerPlacement TopLeft => ComposerPlacement.TopLeft;

    public static ComposerPlacement Attached => ComposerPlacement.Attached;

    public bool IsDefault => OffsetX == 0 && OffsetY == 0;

    public ComposerPosition With(double offsetX, double offsetY) => new(offsetX, offsetY);
}
