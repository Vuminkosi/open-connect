namespace OpenConnect.Components.Contracts;

/// <summary>Options configuring composer behavior and presentation.</summary>
public sealed record OpenConnectComposerOptions
{
    public bool Enabled { get; set; } = true;
    public string AssistantName { get; set; } = "Assistant";
    public ComposerPlacement Placement { get; set; } = ComposerPlacement.BottomRight;
    public ComposerPlacement Position
    {
        get => Placement;
        set => Placement = value;
    }

    public bool EnableTaskProgress { get; set; } = true;
    public bool EnableComponentDiscovery { get; set; } = true;
    public bool EnableVoiceInput { get; set; }
    public bool EnableVoiceOutput { get; set; }
    public ComposerCloseBehavior CloseBehavior { get; set; } = ComposerCloseBehavior.MinimizeOnly;
    public ComposerHistoryPresentation History { get; set; } = ComposerHistoryPresentation.LatestOnly;
    public ComposerInputOptions Input { get; set; } = new();
    public ComposerVoiceOptions Voice { get; set; } = new();
    public bool InitiallyOpen { get; set; }
    public bool PersistPosition { get; set; }
    public ComposerAppearanceOptions Appearance { get; set; } = new();
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
