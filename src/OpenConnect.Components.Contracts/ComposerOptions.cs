namespace OpenConnect.Components.Contracts;

/// <summary>Options configuring composer behavior and presentation.</summary>
public sealed record OpenConnectComposerOptions
{
    public string AssistantName { get; init; } = "Assistant";
    public ComposerPlacement Placement { get; init; } = ComposerPlacement.BottomRight;
    public ComposerCloseBehavior CloseBehavior { get; init; } = ComposerCloseBehavior.MinimizeOnly;
    public ComposerHistoryPresentation History { get; init; } = ComposerHistoryPresentation.LatestOnly;
    public ComposerInputOptions Input { get; init; } = new();
    public ComposerVoiceOptions Voice { get; init; } = new();
    public bool InitiallyOpen { get; init; }
    public bool PersistPosition { get; init; }
    public ComposerAppearanceOptions Appearance { get; init; } = new();
}

/// <summary>Text input settings.</summary>
public sealed record ComposerInputOptions
{
    public int MaxCharacters { get; init; } = 8000;
    public int MaxLines { get; init; } = 5;
    public ComposerInputAction Action { get; init; } = ComposerInputAction.SendOnly;
}

/// <summary>Voice interaction settings.</summary>
public sealed record ComposerVoiceOptions
{
    public bool ShowWhenAvailable { get; init; }
    public bool AllowRecorded { get; init; }
    public bool AllowLive { get; init; }
    public bool EnableAutoplayAfterGesture { get; init; }
}

/// <summary>Visual appearance settings.</summary>
public sealed record ComposerAppearanceOptions;
