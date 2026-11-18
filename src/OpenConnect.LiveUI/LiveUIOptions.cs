namespace OpenConnect.LiveUI;

public sealed class LiveUIOptions
{
    public LiveUIDiscoveryOptions Discovery { get; set; } = new();
    public LiveUIInteractionOptions Interaction { get; set; } = new();
    public LiveUINarrationOptions Narration { get; set; } = new();
    public LiveUISecurityOptions Security { get; set; } = new();
}

public enum LiveUIDiscoveryMode { CompiledFirst, RuntimeOnly }

public sealed class LiveUIDiscoveryOptions
{
    public LiveUIDiscoveryMode Mode { get; set; } = LiveUIDiscoveryMode.CompiledFirst;
    public bool EnableRuntimeFallback { get; set; } = true;
    public int MaximumTargetsPerRoute { get; set; } = 500;
}

public sealed class LiveUIInteractionOptions
{
    public bool EnableNavigation { get; set; } = true;
    public bool EnableFiltering { get; set; } = true;
    public bool EnableHighlighting { get; set; } = true;
    public TimeSpan DefaultActionTimeout { get; set; } = TimeSpan.FromSeconds(10);
}

public sealed class LiveUINarrationOptions
{
    public bool Enabled { get; set; }
    public bool SynchronizeWithSpeech { get; set; }
}

public sealed class LiveUISecurityOptions
{
    public bool RequireConfirmationForWrites { get; set; } = true;
    public int MaximumActionIdLength { get; set; } = 128;
}
